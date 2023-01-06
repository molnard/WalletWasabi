using NBitcoin;
using NBitcoin.RPC;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WalletWasabi.Interfaces;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Backend.Rounds.CoinJoinStorage;

namespace WalletWasabi.WabiSabi.Backend.Banning;

public class RoundVerifier
{
	public RoundVerifier(uint256 roundId, Whitelist whitelist, CoinJoinIdStore coinJoinIdStore, CoinVerifierApiClient coinVerifierApiClient, WabiSabiConfig wabiSabiConfig)
	{
		RoundId = roundId;
		Whitelist = whitelist;
		CoinJoinIdStore = coinJoinIdStore;
		CoinVerifierApiClient = coinVerifierApiClient;
		WabiSabiConfig = wabiSabiConfig;
	}

	public bool IsFinished { get; private set; }
	public uint256 RoundId { get; }
	public Whitelist Whitelist { get; }
	public CoinJoinIdStore CoinJoinIdStore { get; }
	public CoinVerifierApiClient CoinVerifierApiClient { get; }
	public WabiSabiConfig WabiSabiConfig { get; }
	public bool IsStarted { get; private set; }

	private Channel<AliceVerifyItem> AliceVerifyItems { get; } = Channel.CreateUnbounded<AliceVerifyItem>();
	public ConcurrentDictionary<Coin, TaskCompletionSource<CoinVerifyInfo>> CoinResults { get; } = new();
	private Task? Task { get; set; }

	public void AddAlice(Alice alice, GetTxOutResponse txOutResponse)
	{
		AliceVerifyItems.Writer.TryWrite(new AliceVerifyItem(alice.Coin, alice, txOutResponse));
		CoinResults.TryAdd(alice.Coin, new TaskCompletionSource<CoinVerifyInfo>());
	}

	internal void Close()
	{
		throw new NotImplementedException();
	}

	public void Start(CancellationToken cancel)
	{
		if (IsStarted)
		{
			throw new InvalidOperationException($"{nameof(RoundVerifier)} already started.");
		}

		IsStarted = true;

		Task = VerifyAllAsync(cancel);
	}

	private async Task VerifyAllAsync(CancellationToken cancel)
	{
		do
		{
			await AliceVerifyItems.Reader.WaitToReadAsync(cancel).ConfigureAwait(false);
			await foreach (var alice in AliceVerifyItems.Reader.ReadAllAsync(cancel))
			{
				var taskCompletionSourceToSet = CoinResults[alice.Coin];
				var _ = async () =>
				{
					try
					{
						var coinVerifyInfo = await VerifyCoinAsync(alice, taskCompletionSourceToSet).ConfigureAwait(false);
						taskCompletionSourceToSet.SetResult(coinVerifyInfo);
					}
					catch (OperationCanceledException)
					{
						taskCompletionSourceToSet.TrySetCanceled();
					}
					catch (Exception ex)
					{
						// This cannot throw otherwise unobserved.
						taskCompletionSourceToSet.TrySetException(ex);
					}
				};
			}
		}
		while (!cancel.IsCancellationRequested);
	}

	private async Task<CoinVerifyInfo> VerifyCoinAsync(AliceVerifyItem aliceVerifyItem, TaskCompletionSource<CoinVerifyInfo> taskCompletionSourceToSet)
	{
		using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
		var coin = aliceVerifyItem.Coin;

		// Check if address is whitelisted.
		if (Whitelist.TryGet(coin.Outpoint, out _))
		{
			return new CoinVerifyInfo(false, coin);
		}

		// Check if address is from a coinjoin.
		if (CoinJoinIdStore.Contains(coin.Outpoint.Hash))
		{
			return new CoinVerifyInfo(false, coin);
		}

		// Check if coin is not paying - one hop.
		if (aliceVerifyItem.Alice.IsPayingZeroCoordinationFee)
		{
			return new CoinVerifyInfo(false, coin);
		}

		// Big coin, under CA minimum confirmation requirement cannot register.
		if (coin.Amount >= WabiSabiConfig.CoinVerifierRequiredConfirmationAmount
			&& aliceVerifyItem.TxOutResponse.Confirmations < WabiSabiConfig.CoinVerifierRequiredConfirmation)
		{
			// https://github.com/zkSNACKs/CoinVerifier/issues/11
			// Should not ban but removed.
			return new CoinVerifyInfo(false, coin);
		}

		var apiResponse = await CoinVerifierApiClient.SendRequestAsync(coin.ScriptPubKey, cts.Token).ConfigureAwait(false);
		bool shouldBanUtxo = CheckForFlags(apiResponse);

		return new CoinVerifyInfo(shouldBanUtxo, coin);
	}

	private bool CheckForFlags(ApiResponseItem response)
	{
		bool shouldBan = false;

		if (WabiSabiConfig.RiskFlags is null)
		{
			return shouldBan;
		}

		var flagIds = response.Cscore_section.Cscore_info.Select(cscores => cscores.Id);

		if (flagIds.Except(WabiSabiConfig.RiskFlags).Any())
		{
			var unknownIds = flagIds.Except(WabiSabiConfig.RiskFlags).ToList();
			unknownIds.ForEach(id => Logger.LogWarning($"Flag {id} is unknown for the backend!"));
		}

		shouldBan = flagIds.Any(id => WabiSabiConfig.RiskFlags.Contains(id));

		return shouldBan;
	}

	private record AliceVerifyItem(Coin Coin, Alice Alice, GetTxOutResponse TxOutResponse);
}
