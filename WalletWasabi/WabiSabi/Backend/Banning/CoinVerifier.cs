using NBitcoin;
using NBitcoin.RPC;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.Rounds.CoinJoinStorage;
using WalletWasabi.WabiSabi.Backend.Statistics;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.WabiSabi.Backend.Banning;

public record CoinVerifyInfo(bool ShouldBan, Coin Coin);

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

	private Dictionary<uint256, RoundVerifier> RoundVerifiers { get; } = new Dictionary<uint256, RoundVerifier>();

	private bool CheckIfAlreadyVerified(Coin coin)
	{
		// Step 1: Check if address is whitelisted.
		if (Whitelist.TryGet(coin.Outpoint, out _))
		{
			return true;
		}

		// Step 2: Check if address is from a coinjoin.
		if (CoinJoinIdStore.Contains(coin.Outpoint.Hash))
		{
			return true;
		}

		return false;
	}

	public async IAsyncEnumerable<CoinVerifyInfo> VerifyCoinsAsync(IEnumerable<Coin> coinsToCheck, [EnumeratorCancellation] CancellationToken cancellationToken, string roundId = "")
	{
		// TODO: impelement NEW logic!!!
		var before = DateTimeOffset.UtcNow;

		using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(30));
		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token, cancellationToken);

		var lastChangeId = Whitelist.ChangeId;
		Whitelist.RemoveAllExpired(WabiSabiConfig.ReleaseFromWhitelistAfter);

		var scriptsToCheck = new HashSet<Script>();
		var innocentsCounter = 0;

		foreach (var coin in coinsToCheck)
		{
			if (CheckIfAlreadyVerified(coin))
			{
				innocentsCounter++;
				yield return new CoinVerifyInfo(false, coin);
			}
			else
			{
				scriptsToCheck.Add(coin.ScriptPubKey);
			}
		}

		Logger.LogInfo($"{innocentsCounter} out of {coinsToCheck.Count()} utxo is already verified in Round({roundId}).");
		await foreach (var response in CoinVerifierApiClient.VerifyScriptsAsync(scriptsToCheck, linkedCts.Token))
		{
			bool shouldBanUtxo = CheckForFlags(response.ApiResponseItem);

			// Find all coins with the same script (address reuse).
			foreach (var coin in coinsToCheck.Where(c => c.ScriptPubKey == response.ScriptPubKey))
			{
				if (!shouldBanUtxo)
				{
					Whitelist.Add(coin.Outpoint);
				}

				yield return new CoinVerifyInfo(shouldBanUtxo, coin);
			}
		}

		if (Whitelist.ChangeId != lastChangeId)
		{
			Whitelist.WriteToFile();
		}

		var duration = DateTimeOffset.UtcNow - before;
		RequestTimeStatista.Instance.Add("verifier-period", duration);
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

	internal void AddAlice(Alice alice, GetTxOutResponse txOutResponse)
	{
		if (RoundVerifiers.TryGetValue(alice.Round.Id, out var roundVerifier))
		{
			roundVerifier.AddAlice(alice, txOutResponse);
			return;
		}

		throw new InvalidOperationException($"Could not find {nameof(RoundVerifier)} for round:'{alice.Round.Id}'.");
	}

	internal void StepRoundVerifiers(IEnumerable<RoundState> roundStates, CancellationToken cancel)
	{
		// Handling new round additions.
		var newRoundIds = roundStates.Where(rs => !RoundVerifiers.ContainsKey(rs.Id)).Select(rs => rs.Id);

		foreach (var newRoundId in newRoundIds)
		{
			var newVerifier = new RoundVerifier(newRoundId);
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
						roundVerifier.Start();
					}
					break;

				default:
					roundVerifier.Close();
					break;
			}
		}
	}
}
