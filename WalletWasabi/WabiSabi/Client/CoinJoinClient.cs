using Microsoft.Extensions.Hosting;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Crypto.ZeroKnowledge;
using WalletWasabi.Logging;
using WalletWasabi.Tor.Http;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Crypto;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using WalletWasabi.Wallets;

namespace WalletWasabi.WabiSabi.Client
{
	public class CoinJoinClient : BackgroundService, IDisposable
	{
		private bool _disposedValue;
		private ZeroCredentialPool ZeroAmountCredentialPool { get; } = new();
		private ZeroCredentialPool ZeroVsizeCredentialPool { get; } = new();
		private ClientRound Round { get; }
		public IArenaRequestHandler ArenaRequestHandler { get; }
		public Kitchen Kitchen { get; }
		public KeyManager Keymanager { get; }
		private SecureRandom SecureRandom { get; }
		private CancellationTokenSource DisposeCts { get; } = new();
		private IEnumerable<Coin> Coins { get; set; }
		private Random Random { get; } = new();

		public CoinJoinClient(
			ClientRound round,
			IArenaRequestHandler arenaRequestHandler,
			IEnumerable<Coin> coins,
			Kitchen kitchen,
			KeyManager keymanager)
		{
			Round = round;
			ArenaRequestHandler = arenaRequestHandler;
			Kitchen = kitchen;
			Keymanager = keymanager;
			SecureRandom = new SecureRandom();
			Coins = coins;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			try
			{
				var aliceClients = CreateAliceClients();

				// Register coins.
				aliceClients = await RegisterCoinsAsync(aliceClients, stoppingToken).ConfigureAwait(false);

				// Confirm coins.
				aliceClients = await ConfirmConnectionsAsync(aliceClients, stoppingToken).ConfigureAwait(false);

				// Planning
				ConstructionState constructionState = Round.CoinjoinState.AssertConstruction();
				var decompositionPlan = DecomposeAmounts(constructionState, stoppingToken);

				// Output registration.
				await RegisterOutputsAsync(null, stoppingToken).ConfigureAwait(false);

				SigningState signingState = Round.CoinjoinState.AssertSigning();
				var unsignedCoinJoin = signingState.CreateUnsignedTransaction();

				// Sanity check.
				SanityCheck(decompositionPlan, unsignedCoinJoin, stoppingToken);

				// Send signature.
				await SignTransactionAsync(aliceClients, unsignedCoinJoin, stoppingToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				// The game is over for this round, no fallback mechanism. In the next round we will create another CoinJoinClient and try again.
			}
		}

		public async Task StartMixingCoinsAsync()
		{
			await StartAsync(DisposeCts.Token).ConfigureAwait(false);
		}

		private IEnumerable<AliceClient> CreateAliceClients()
		{
			List<AliceClient> aliceClients = new();
			foreach (var coin in Coins)
			{
				var aliceArenaClient = new ArenaClient(
					Round.AmountCredentialIssuerParameters,
					Round.VsizeCredentialIssuerParameters,
					ZeroAmountCredentialPool,
					ZeroVsizeCredentialPool,
					ArenaRequestHandler,
					SecureRandom);

				var hdKey = Keymanager.GetSecrets(Kitchen.SaltSoup(), coin.ScriptPubKey.WitHash.ScriptPubKey).Single();
				var secret = hdKey.PrivateKey.GetBitcoinSecret(Keymanager.GetNetwork());
				aliceClients.Add(new AliceClient(Round.Id, aliceArenaClient, coin, Round.FeeRate, secret));
			}
			return aliceClients;
		}

		private async Task<IEnumerable<AliceClient>> RegisterCoinsAsync(IEnumerable<AliceClient> aliceClientsToRegister, CancellationToken stoppingToken)
		{
			List<AliceClient> registeredAliceClients = new();

			foreach (var aliceClient in aliceClientsToRegister)
			{
				try
				{
					await aliceClient.RegisterInputAsync(stoppingToken).ConfigureAwait(false);
					registeredAliceClients.Add(aliceClient);
				}
				catch (Exception ex)
				{
					Logger.LogWarning($"Round ({Round.Id}), Alice ({aliceClient.AliceId}): {nameof(AliceClient.RegisterInputAsync)} failed, reason:'{ex}'.");
				}
			}

			if (registeredAliceClients.Count == 0)
			{
				throw new InvalidOperationException($"Round ({Round.Id}): No inputs were registered.");
			}

			if (registeredAliceClients.Sum(a => a.Coin.Amount) < Money.Coins(0.001m)) //TODO: what is the minimum here?
			{
				throw new InvalidOperationException($"Round ({Round.Id}): Could not register enough amount.");
			}

			return registeredAliceClients;
		}

		private async Task<IEnumerable<AliceClient>> ConfirmConnectionsAsync(IEnumerable<AliceClient> aliceClients, CancellationToken stoppingToken)
		{
			List<AliceClient> confirmedAliceClients = new();
			foreach (var aliceClient in aliceClients)
			{
				try
				{
					await aliceClient.ConfirmConnectionAsync(TimeSpan.FromMilliseconds(Random.Next(1000, 5000)), stoppingToken).ConfigureAwait(false);
					confirmedAliceClients.Add(aliceClient);
					await Task.Delay(Random.Next(0, 1000), stoppingToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Logger.LogWarning($"Round ({Round.Id}), Alice ({aliceClient.AliceId}): {nameof(AliceClient.ConfirmConnectionAsync)} failed, reason:'{ex}'.");
				}
			}

			return confirmedAliceClients;
		}

		private async Task RegisterOutputsAsync(IEnumerable<(Money Amount, HdPubKey Pubkey, Credential amountCredential, Credential vsizeCredential)> outputs, CancellationToken stoppingToken)
		{
			ArenaClient bobArenaClient = new(
				Round.AmountCredentialIssuerParameters,
				Round.VsizeCredentialIssuerParameters,
				ZeroAmountCredentialPool,
				ZeroVsizeCredentialPool,
				ArenaRequestHandler,
				SecureRandom);

			BobClient bobClient = new(Round.Id, bobArenaClient);

			foreach (var output in outputs)
			{
				await bobClient.RegisterOutputAsync(
					output.Amount,
					output.Pubkey.PubKey.WitHash.ScriptPubKey,
					new[] { output.amountCredential },
					new[] { output.vsizeCredential },
					stoppingToken).ConfigureAwait(false);

				await Task.Delay(Random.Next(0, 1000), stoppingToken).ConfigureAwait(false);
			}
		}

		private IEnumerable<(Money Amount, HdPubKey Pubkey)> DecomposeAmounts(ConstructionState construction, CancellationToken stoppingToken)
		{
			const int Count = 4;

			// Simple decomposer.
			Money total = Coins.Sum(c => c.Amount) - Round.FeeRate.GetFee(Helpers.Constants.P2wpkhInputVirtualSize);
			Money amount = total / Count;

			List<Money> amounts = Enumerable.Repeat(Money.Satoshis(amount), Count - 1).ToList();
			amounts.Add(total - amounts.Sum());

			return amounts.Select(amount => (amount, Keymanager.GenerateNewKey("", KeyState.Locked, true, true))).ToArray(); // Keymanager threadsafe => no!?
		}

		private void SanityCheck(IEnumerable<(Money Amount, HdPubKey Pubkey)> outputs, Transaction unsignedCoinJoinTransaction, CancellationToken stoppingToken)
		{
			if (outputs.All(output => unsignedCoinJoinTransaction.Outputs.Select(o => o.ScriptPubKey.WitHash.ScriptPubKey).Contains(output.Pubkey.PubKey.ScriptPubKey)))
			{
				throw new InvalidOperationException($"Round ({Round.Id}): My output is missing.");
			}
		}

		private async Task SignTransactionAsync(IEnumerable<AliceClient> aliceClients, Transaction unsignedCoinJoinTransaction, CancellationToken stoppingToken)
		{
			foreach (var aliceClient in aliceClients)
			{
				await aliceClient.SignTransactionAsync(unsignedCoinJoinTransaction, stoppingToken).ConfigureAwait(false);
			}
		}

		public async Task StopAsync()
		{
			await StopAsync().ConfigureAwait(false);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!_disposedValue)
			{
				if (disposing)
				{
					DisposeCts.Cancel();
					SecureRandom.Dispose();
				}
				_disposedValue = true;
			}
		}

		public void Dispose()
		{
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}
	}
}
