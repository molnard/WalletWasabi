using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Backend.Rounds.CoinJoinStorage;
using WalletWasabi.WabiSabi.Backend.Statistics;

namespace WalletWasabi.WabiSabi.Backend.Banning;

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

	public async IAsyncEnumerable<CoinVerifyInfo> VerifyCoinsAsync(IEnumerable<Coin> coinsToCheck, [EnumeratorCancellation] CancellationToken cancellationToken, string roundId = "")
	{
		var before = DateTimeOffset.UtcNow;

		using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(30));
		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token, cancellationToken);

		var lastChangeId = Whitelist.ChangeId;
		Whitelist.RemoveAllExpired(WabiSabiConfig.ReleaseFromWhitelistAfter);

		var scriptsToCheck = new HashSet<Script>();
		var innocentsCounter = 0;

		foreach (var coin in coinsToCheck)
		{
			// Step 1: Check if the address is whitelisted.
			if (Whitelist.TryGet(coin.Outpoint, out _))
			{
				innocentsCounter++;
				yield return new CoinVerifyInfo(Coin: coin, SuccessfulVerification: true, ShouldBan: false, Reason: CoinVerifierReason.Whitelisted, ApiResponseItem: null);
			}
			// Step 2: Check if the coin is from a coinjoin transaction.
			else if (CoinJoinIdStore.Contains(coin.Outpoint.Hash))
			{
				innocentsCounter++;
				yield return new CoinVerifyInfo(Coin: coin, SuccessfulVerification: true, ShouldBan: false, Reason: CoinVerifierReason.FromCoinjoin, ApiResponseItem: null);
			}
			else
			{
				scriptsToCheck.Add(coin.ScriptPubKey);
			}
		}

		Logger.LogInfo($"{innocentsCounter} out of {coinsToCheck.Count()} utxo is already verified in Round({roundId}).");
		await foreach (var response in CoinVerifierApiClient.VerifyScriptsAsync(scriptsToCheck, linkedCts.Token).ConfigureAwait(false))
		{
			bool shouldBanUtxo = CheckForFlags(response.ApiResponseItem);

			// Find all coins with the same script (address reuse).
			foreach (var coin in coinsToCheck.Where(c => c.ScriptPubKey == response.ScriptPubKey))
			{
				if (!shouldBanUtxo)
				{
					Whitelist.Add(coin.Outpoint);
				}
				yield return new CoinVerifyInfo(Coin: coin, SuccessfulVerification: true, ShouldBan: shouldBanUtxo, Reason: CoinVerifierReason.RemoteApiCheck, ApiResponseItem: response.ApiResponseItem);
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
}
