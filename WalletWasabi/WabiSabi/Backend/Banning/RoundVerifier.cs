using NBitcoin;
using NBitcoin.RPC;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WalletWasabi.CoinJoin.Coordinator.Participants;
using WalletWasabi.Interfaces;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Backend.DoSPrevention;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Backend.Rounds.CoinJoinStorage;

namespace WalletWasabi.WabiSabi.Backend.Banning;

public class RoundVerifier
{
	public RoundVerifier(uint256 roundId, Whitelist whitelist, CoinJoinIdStore coinJoinIdStore, CoinVerifierApiClient coinVerifierApiClient, WabiSabiConfig wabiSabiConfig, Prison prison)
	{
		RoundId = roundId;
		Whitelist = whitelist;
		CoinJoinIdStore = coinJoinIdStore;
		CoinVerifierApiClient = coinVerifierApiClient;
		WabiSabiConfig = wabiSabiConfig;
		Prison = prison;
	}

	public bool IsFinished { get; private set; }
	public uint256 RoundId { get; }
	public Whitelist Whitelist { get; }
	public CoinJoinIdStore CoinJoinIdStore { get; }
	public CoinVerifierApiClient CoinVerifierApiClient { get; }
	public WabiSabiConfig WabiSabiConfig { get; }
	public Prison Prison { get; }
	public bool IsStarted { get; private set; }

	private Channel<CoinVerifyItem> CoinVerifyItems { get; } = Channel.CreateUnbounded<CoinVerifyItem>();
	private ConcurrentDictionary<Coin, TaskCompletionSource<CoinVerifyInfo>> CoinResults { get; } = new();
	public bool IsClosed { get; private set; }

	public void AddCoin(Coin coin, GetTxOutResponse txOutResponse, bool? oneHopCoin = null)
	{
		if (IsClosed)
		{
			throw new InvalidOperationException($"Adding coin for {nameof(RoundVerifier)} is closed for round '{RoundId}'.");
		}

		CoinVerifyItems.Writer.TryWrite(new CoinVerifyItem(coin, txOutResponse, oneHopCoin));
		CoinResults.TryAdd(coin, new TaskCompletionSource<CoinVerifyInfo>());
	}

	public IEnumerable<Task<CoinVerifyInfo>> CloseAndGetCoinResultTasks()
	{
		if (!IsClosed)
		{
			IsClosed = true;
			CoinVerifyItems.Writer.Complete();
		}
		return CoinResults.Values.Select(x => x.Task);
	}

	public void Start(CancellationToken cancel)
	{
		if (IsStarted)
		{
			throw new InvalidOperationException($"{nameof(RoundVerifier)} already started for round '{RoundId}'.");
		}

		IsStarted = true;

		_ = VerifyAllAsync(cancel);
	}

	private async Task VerifyAllAsync(CancellationToken cancel)
	{
		try
		{
			do
			{
				var itemsAvailable = await CoinVerifyItems.Reader.WaitToReadAsync(cancel).ConfigureAwait(false);

				if (itemsAvailable)
				{
					await foreach (var coinVerifyItem in CoinVerifyItems.Reader.ReadAllAsync(cancel))
					{
						var taskCompletionSourceToSet = CoinResults[coinVerifyItem.Coin];
						var _ = async () =>
						{
							try
							{
								// We will wait 2 minutes for the result, no matter if the outside observer is waiting for the result or not.
								// Next time the coin wants to register the information will be available.
								using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
								var coinVerifyInfo = await VerifyCoinAsync(coinVerifyItem, cts.Token).ConfigureAwait(false);

								// Handle the results here. Maybe nobody is looking at taskCompletionSourceToSet, because there was a timeout.
								if (!coinVerifyInfo.ShouldBan)
								{
									Whitelist.Add(coinVerifyInfo.Coin.Outpoint);
								}
								else
								{
									Prison.Ban(coinVerifyInfo.Coin.Outpoint, RoundId, isLongBan: true);
								}

								taskCompletionSourceToSet.SetResult(coinVerifyInfo);
							}
							catch (Exception ex)
							{
								// This cannot throw otherwise unobserved.
								taskCompletionSourceToSet.TrySetException(new CoinVerifyItemException(coinVerifyItem.Coin, ex));
							}
						};
					}
				}

				if (IsClosed)
				{
					// We are out of items and no more expected.
					break;
				}

				cancel.ThrowIfCancellationRequested();
			}
			while (true);
		}
		catch (OperationCanceledException)
		{
			// Cancel is OK.
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	private async Task<CoinVerifyInfo> VerifyCoinAsync(CoinVerifyItem coinVerifyItem, CancellationToken cancellationToken)
	{
		var coin = coinVerifyItem.Coin;

		// Check if coin is one hop.
		if (coinVerifyItem.OneHopCoin is true)
		{
			return new CoinVerifyInfo(false, false, coin);
		}

		// Check if address is whitelisted.
		if (Whitelist.TryGet(coin.Outpoint, out _))
		{
			return new CoinVerifyInfo(false, false, coin);
		}

		// Check if address is from a coinjoin.
		if (CoinJoinIdStore.Contains(coin.Outpoint.Hash))
		{
			return new CoinVerifyInfo(false, false, coin);
		}

		// Coin amount bigger than x and under CA minimum confirmation requirement cannot register.
		if (coin.Amount >= WabiSabiConfig.CoinVerifierRequiredConfirmationAmount
			&& coinVerifyItem.TxOutResponse.Confirmations < WabiSabiConfig.CoinVerifierRequiredConfirmation)
		{
			// https://github.com/zkSNACKs/CoinVerifier/issues/11
			// Should not ban but removed.
			return new CoinVerifyInfo(ShouldBan: false, ShouldRemove: true, coin);
		}

		var apiResponse = await CoinVerifierApiClient.SendRequestAsync(coin.ScriptPubKey, cancellationToken).ConfigureAwait(false);
		bool shouldBanUtxo = CheckForFlags(apiResponse);

		return new CoinVerifyInfo(ShouldBan: shouldBanUtxo, ShouldRemove: shouldBanUtxo, coin);
	}

	private bool CheckForFlags(ApiResponseItem response)
	{
		bool shouldBan = false;

		if (WabiSabiConfig.RiskFlags is null)
		{
			return false;
		}

		var flagIds = response.Cscore_section.Cscore_info.Select(cscores => cscores.Id);

		if (flagIds.Except(WabiSabiConfig.RiskFlags).Any())
		{
			var unknownIds = flagIds.Except(WabiSabiConfig.RiskFlags).ToList();
			unknownIds.ForEach(id => Logger.LogWarning($"Flag {id} is unknown for the backend for round '{RoundId}'."));
		}

		shouldBan = flagIds.Any(id => WabiSabiConfig.RiskFlags.Contains(id));

		return shouldBan;
	}

	private record CoinVerifyItem(Coin Coin, GetTxOutResponse TxOutResponse, bool? OneHopCoin = null);
}
