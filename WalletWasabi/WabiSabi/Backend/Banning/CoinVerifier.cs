using NBitcoin;
using NBitcoin.RPC;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.Rounds.CoinJoinStorage;
using WalletWasabi.WabiSabi.Backend.Statistics;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.WabiSabi.Backend.Banning;

public record CoinVerifyInfo(bool ShouldBan, bool ShouldRemove, Coin Coin);

public class CoinVerifier
{
	public CoinVerifier(CoinJoinIdStore coinJoinIdStore, CoinVerifierApiClient apiClient, Whitelist whitelist, WabiSabiConfig wabiSabiConfig)
	{
		CoinJoinIdStore = coinJoinIdStore;
		CoinVerifierApiClient = apiClient;
		Whitelist = whitelist;
		WabiSabiConfig = wabiSabiConfig;
	}

	// Blank constructor used for testing
	internal CoinVerifier(CoinJoinIdStore coinJoinIdStore, CoinVerifierApiClient apiClient, WabiSabiConfig wabiSabiConfig)
	{
		CoinJoinIdStore = coinJoinIdStore;
		CoinVerifierApiClient = apiClient;
		Whitelist = new();
		WabiSabiConfig = wabiSabiConfig;
	}

	public Whitelist Whitelist { get; }
	public WabiSabiConfig WabiSabiConfig { get; }
	private CoinJoinIdStore CoinJoinIdStore { get; }
	private CoinVerifierApiClient CoinVerifierApiClient { get; }

	private Dictionary<uint256, RoundVerifier> RoundVerifiers { get; } = new();

	public async IAsyncEnumerable<CoinVerifyInfo> VerifyCoinsAsync([EnumeratorCancellation] CancellationToken cancellationToken, uint256 roundId)
	{
		var before = DateTimeOffset.UtcNow;

		var roundVerifier = RoundVerifiers[roundId];

		var tasks = roundVerifier.CloseAndGetCoinResultsTasks();

		do
		{
			// TODO handle timeout!!
			var completedTask = await Task.WhenAny(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
			var coinVerifyInfo = await completedTask.WaitAsync(cancellationToken).ConfigureAwait(false);

			if (!coinVerifyInfo.ShouldBan)
			{
				Whitelist.Add(coinVerifyInfo.Coin.Outpoint);
			}

			yield return coinVerifyInfo;
		}
		while (!cancellationToken.IsCancellationRequested);

		Whitelist.WriteToFileIfChanged();

		var duration = DateTimeOffset.UtcNow - before;
		RequestTimeStatista.Instance.Add("verifier-period", duration);
	}

	public void AddCoin(uint256 roundId, Coin coin, GetTxOutResponse txOutResponse, bool? oneHopCoin = null)
	{
		if (RoundVerifiers.TryGetValue(roundId, out var roundVerifier))
		{
			roundVerifier.AddCoin(coin, txOutResponse, oneHopCoin);
			return;
		}

		throw new InvalidOperationException($"Could not find {nameof(RoundVerifier)} for round:'{roundId}'.");
	}

	internal void StepRoundVerifiers(IEnumerable<RoundState> roundStates, CancellationToken cancel)
	{
		// Handling new round additions.
		var newRoundIds = roundStates.Where(rs => !RoundVerifiers.ContainsKey(rs.Id)).Select(rs => rs.Id);

		foreach (var newRoundId in newRoundIds)
		{
			var newVerifier = new RoundVerifier(newRoundId, Whitelist, CoinJoinIdStore, CoinVerifierApiClient, WabiSabiConfig);
			RoundVerifiers.Add(newRoundId, newVerifier);
		}

		foreach (var roundVerifier in RoundVerifiers.Values.ToArray())
		{
			if (roundVerifier.IsFinished)
			{
				// Remove RoundVerifiers when they are finished.
				RoundVerifiers.Remove(roundVerifier.RoundId);
				continue;
			}

			var roundState = roundStates.SingleOrDefault(rs => rs.Id == roundVerifier.RoundId);

			switch (roundState?.Phase)
			{
				case Rounds.Phase.InputRegistration:
					if (!roundVerifier.IsStarted
						&& roundState.InputRegistrationEnd - DateTimeOffset.UtcNow < WabiSabiConfig.CoinVerifierStartBefore)
					{
						// Start verifications before the end Input registration.
						roundVerifier.Start(cancel);
					}
					break;

				default:
					_ = roundVerifier.CloseAndGetCoinResultsTasks();
					break;
			}
		}
	}
}
