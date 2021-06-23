using Microsoft.Extensions.Hosting;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Crypto.ZeroKnowledge;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Client.CredentialDependencies;
using WalletWasabi.WabiSabi.Crypto;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.Decomposition;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using WalletWasabi.Wallets;

namespace WalletWasabi.WabiSabi.Client
{
	public class CoinJoinClient
	{
		public CoinJoinClient(
			IWabiSabiApiRequestHandler arenaRequestHandler,
			IEnumerable<Coin> coins,
			Kitchen kitchen,
			KeyManager keymanager,
			RoundStateUpdater roundStatusUpdater)
		{
			ArenaRequestHandler = arenaRequestHandler;
			Kitchen = kitchen;
			Keymanager = keymanager;
			RoundStatusUpdater = roundStatusUpdater;
			SecureRandom = new SecureRandom();
			Coins = coins;
		}

		private ZeroCredentialPool ZeroAmountCredentialPool { get; } = new();
		private ZeroCredentialPool ZeroVsizeCredentialPool { get; } = new();
		private IEnumerable<Coin> Coins { get; set; }
		private SecureRandom SecureRandom { get; } = new SecureRandom();
		private Random Random { get; } = new();
		public IWabiSabiApiRequestHandler ArenaRequestHandler { get; }
		public Kitchen Kitchen { get; }
		public KeyManager Keymanager { get; }
		private RoundStateUpdater RoundStatusUpdater { get; }

		public async Task StartCoinJoinAsync(CancellationToken cancellationToken, IEnumerable<Money>? forcedOutputDenominations = null)
		{
			var roundState = await RoundStatusUpdater.CreateRoundAwaiter(roundState => roundState.Phase == Phase.InputRegistration, cancellationToken).ConfigureAwait(false);
			var constructionState = roundState.Assert<ConstructionState>();

			// Calculate outputs values
			var outputValues = DecomposeAmounts(roundState.FeeRate, roundState.CoinjoinState.Parameters.AllowedOutputAmounts.Min, forcedOutputDenominations);

			// Get all locked internal keys we have and assert we have enough.
			Keymanager.AssertLockedInternalKeysIndexed(howMany: outputValues.Count());
			var allLockedInternalKeys = Keymanager.GetKeys(x => x.IsInternal && x.KeyState == KeyState.Locked);
			var outputTxOuts = outputValues.Zip(allLockedInternalKeys, (amount, hdPubKey) => new TxOut(amount, hdPubKey.P2wpkhScript));

			// TODO remove
			var mutableTxOuts = outputTxOuts.ToArray();
			var missing = Coins.Sum(c => c.EffectiveValue(roundState.FeeRate)) - outputTxOuts.Sum(o => o.EffectiveCost(roundState.FeeRate));
			mutableTxOuts[0].Value += missing;
			outputTxOuts = mutableTxOuts;

			DependencyGraph dependencyGraph = DependencyGraph.ResolveCredentialDependencies(Coins, outputTxOuts, roundState.FeeRate, roundState.MaxVsizeAllocationPerAlice);

			List<AliceClient> aliceClients = CreateAliceClients(roundState);

			// Register coins.
			aliceClients = await RegisterCoinsAsync(aliceClients, cancellationToken).ConfigureAwait(false);

			// Confirm coins.
			// TODO move to dgr.ResolveAsync
			aliceClients = await ConfirmConnectionsAsync(aliceClients, dependencyGraph, roundState.ConnectionConfirmationTimeout, cancellationToken).ConfigureAwait(false);

			// Re-issuances.
			DependencyGraphResolver dgr = new(dependencyGraph, ZeroAmountCredentialPool, ZeroVsizeCredentialPool);
			var bobClient = CreateBobClient(roundState);
			await dgr.StartReissuancesAsync(aliceClients, bobClient, cancellationToken).ConfigureAwait(false);

			// Output registration.
			roundState = await RoundStatusUpdater.CreateRoundAwaiter(roundState.Id, rs => rs.Phase == Phase.OutputRegistration, cancellationToken).ConfigureAwait(false);
			await dgr.StartOutputRegistrationsAsync(outputTxOuts, bobClient, cancellationToken).ConfigureAwait(false);

			// Signing.
			roundState = await RoundStatusUpdater.CreateRoundAwaiter(roundState.Id, rs => rs.Phase == Phase.TransactionSigning, cancellationToken).ConfigureAwait(false);
			var signingState = roundState.Assert<SigningState>();
			var unsignedCoinJoin = signingState.CreateUnsignedTransaction();

			// Sanity check.
			if (!SanityCheck(outputTxOuts, unsignedCoinJoin))
			{
				throw new InvalidOperationException($"Round ({roundState.Id}): My output is missing.");
			}

			// Send signature.
			await SignTransactionAsync(aliceClients, unsignedCoinJoin, cancellationToken).ConfigureAwait(false);
		}

		private List<AliceClient> CreateAliceClients(RoundState roundState)
		{
			List<AliceClient> aliceClients = new();
			foreach (var coin in Coins)
			{
				var aliceArenaClient = new ArenaClient(
					roundState.CreateAmountCredentialClient(ZeroAmountCredentialPool, SecureRandom),
					roundState.CreateVsizeCredentialClient(ZeroVsizeCredentialPool, SecureRandom),
					ArenaRequestHandler);

				var hdKey = Keymanager.GetSecrets(Kitchen.SaltSoup(), coin.ScriptPubKey).Single();
				var secret = hdKey.PrivateKey.GetBitcoinSecret(Keymanager.GetNetwork());
				aliceClients.Add(new AliceClient(roundState.Id, aliceArenaClient, coin, roundState.FeeRate, secret));
			}
			return aliceClients;
		}

		private async Task<List<AliceClient>> RegisterCoinsAsync(IEnumerable<AliceClient> aliceClients, CancellationToken cancellationToken)
		{
			async Task<AliceClient?> RegisterInputTask(AliceClient aliceClient)
			{
				try
				{
					await aliceClient.RegisterInputAsync(cancellationToken).ConfigureAwait(false);
					return aliceClient;
				}
				catch (Exception e)
				{
					Logger.LogWarning($"Round ({aliceClient.RoundId}), Alice ({aliceClient.AliceId}): {nameof(AliceClient.RegisterInputAsync)} failed, reason:'{e}'.");
					return default;
				}
			}

			var registerRequests = aliceClients.Select(RegisterInputTask);
			var completedRequests = await Task.WhenAll(registerRequests).ConfigureAwait(false);

			return completedRequests.Where(x => x is not null).Cast<AliceClient>().ToList();
		}

		// TODO redundant with SmartRequestNode, move confirmation to there and remove
		private IEnumerable<long> AddExtraCredential(IEnumerable<long> valuesToRequest, long sum)
		{
			var nonZeroValues = valuesToRequest.Where(v => v > 0);

			if (nonZeroValues.Count() == ProtocolConstants.CredentialNumber)
			{
				return nonZeroValues;
			}

			var missing = sum - valuesToRequest.Sum();

			if (missing > 0)
			{
				nonZeroValues = nonZeroValues.Append(missing);
			}

			var additionalZeros = ProtocolConstants.CredentialNumber - nonZeroValues.Count();

			return nonZeroValues.Concat(Enumerable.Repeat(0L, additionalZeros));
		}

		private async Task<List<AliceClient>> ConfirmConnectionsAsync(IEnumerable<AliceClient> aliceClients, DependencyGraph dependencyGraph, TimeSpan connectionConfirmationTimeout, CancellationToken cancellationToken)
		{
			async Task<AliceClient?> ConfirmConnectionTask(AliceClient aliceClient, RequestNode node)
			{
				try
				{
					var amountsToRequest = AddExtraCredential(dependencyGraph.OutEdges(node, CredentialType.Amount).Select(e => (long)e.Value),  node.InitialBalance(CredentialType.Amount));
					var vsizesToRequest = AddExtraCredential(dependencyGraph.OutEdges(node, CredentialType.Vsize).Select(e => (long)e.Value), node.InitialBalance(CredentialType.Vsize));

					await aliceClient.ConfirmConnectionAsync(connectionConfirmationTimeout, amountsToRequest, vsizesToRequest, cancellationToken).ConfigureAwait(false);

					return aliceClient;
				}
				catch (Exception e)
				{
					Logger.LogWarning($"Round ({aliceClient.RoundId}), Alice ({aliceClient.AliceId}): {nameof(AliceClient.ConfirmConnectionAsync)} failed, reason:'{e}'.");
					return default;
				}
			}

			var confirmationRequests = Enumerable.Zip(aliceClients, dependencyGraph.Inputs, ConfirmConnectionTask);
			var completedRequests = await Task.WhenAll(confirmationRequests).ConfigureAwait(false);

			return completedRequests.Where(x => x is not null).Cast<AliceClient>().ToList();
		}

		private IEnumerable<Money> DecomposeAmounts(FeeRate feeRate, Money minimumOutputAmount, IEnumerable<Money>? forcedOutputDenominations = null)
		{
			var allDenominations = forcedOutputDenominations is null ? StandardDenomination.Values : forcedOutputDenominations;
			GreedyDecomposer greedyDecomposer = new(allDenominations.Where(x => x >= minimumOutputAmount));
			var sum = Coins.Sum(c => c.EffectiveValue(feeRate));
			return greedyDecomposer.Decompose(sum, feeRate.GetFee(31));
		}

		private IEnumerable<IEnumerable<(ulong RealAmountCredentialValue, ulong RealVsizeCredentialValue, Money Value)>> CreatePlan(
			IEnumerable<ulong> realAmountCredentialValues,
			IEnumerable<ulong> realVsizeCredentialValues,
			IEnumerable<Money> outputValues)
		{
			yield return realAmountCredentialValues.Zip(realVsizeCredentialValues, outputValues, (a, v, o) => (a, v, o));
		}

		private BobClient CreateBobClient(RoundState roundState)
		{
			return new BobClient(
				roundState.Id,
				new(
					roundState.CreateAmountCredentialClient(ZeroAmountCredentialPool, SecureRandom),
					roundState.CreateVsizeCredentialClient(ZeroVsizeCredentialPool, SecureRandom),
					ArenaRequestHandler));
		}

		private bool SanityCheck(IEnumerable<TxOut> expectedOutputs, Transaction unsignedCoinJoinTransaction)
		{
			var coinJoinOutputs = unsignedCoinJoinTransaction.Outputs.Select(o => (o.Value, o.ScriptPubKey));
			var expectedOutputTuples = expectedOutputs.Select(o => (o.Value, o.ScriptPubKey));
			return coinJoinOutputs.IsSuperSetOf(expectedOutputTuples);
		}

		private async Task SignTransactionAsync(IEnumerable<AliceClient> aliceClients, Transaction unsignedCoinJoinTransaction, CancellationToken cancellationToken)
		{
			async Task<AliceClient?> SignTransactionTask(AliceClient aliceClient)
			{
				try
				{
					await aliceClient.SignTransactionAsync(unsignedCoinJoinTransaction, cancellationToken).ConfigureAwait(false);
					return aliceClient;
				}
				catch (Exception e)
				{
					Logger.LogWarning($"Round ({aliceClient.RoundId}), Alice ({{aliceClient.AliceId}}): {nameof(AliceClient.SignTransactionAsync)} failed, reason:'{e}'.");
					return default;
				}
			}

			var signingRequests = aliceClients.Select(SignTransactionTask);
			await Task.WhenAll(signingRequests).ConfigureAwait(false);
		}
	}
}
