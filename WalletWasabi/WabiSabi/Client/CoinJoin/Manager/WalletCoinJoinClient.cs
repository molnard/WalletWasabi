using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Helpers;
using WalletWasabi.WabiSabi.Client.Banning;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.CoinJoinProgressEvents;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.Wallets;

namespace WalletWasabi.WabiSabi.Client.CoinJoin.Manager;

public class WalletCoinJoinClient : BackgroundService
{
	public WalletCoinJoinClient(
		IWallet wallet,
		CoinJoinTrackerFactory coinJoinTrackerFactory,
		CoinRefrigerator coinRefrigerator,
		IWasabiBackendStatusProvider wasabiBackendStatusProvider,
		CoinPrison coinPrison)
	{
		Wallet = wallet;
		CoinJoinTrackerFactory = coinJoinTrackerFactory;
		CoinRefrigerator = coinRefrigerator;
		WasabiBackendStatusProvider = wasabiBackendStatusProvider;
		CoinPrison = coinPrison;
	}

	public event EventHandler<CoinJoinProgressEventArgs>? CoinJoinProgressChanged;

	private IWallet Wallet { get; }
	private CoinJoinTrackerFactory CoinJoinTrackerFactory { get; }
	public CoinRefrigerator CoinRefrigerator { get; }
	public IWasabiBackendStatusProvider WasabiBackendStatusProvider { get; }
	public CoinPrison CoinPrison { get; }
	private CoinJoinTracker? CoinJoinTracker { get; set; }
	private Channel<CoinJoinCommand> CommandChannel { get; } = Channel.CreateUnbounded<CoinJoinCommand>();
	private bool IsOverridePlebStop { get; set; }

	public async Task<CoinJoinTracker> CreateAndStartAsync(IWallet outputWallet, Func<Task<IEnumerable<SmartCoin>>> coinCandidatesFunc, bool stopWhenAllMixed, bool overridePlebStop)
	{
		var coinJoinTracker = await CoinJoinTrackerFactory.CreateAndStartAsync(Wallet, outputWallet, coinCandidatesFunc, stopWhenAllMixed, overridePlebStop).ConfigureAwait(false);
		coinJoinTracker.WalletCoinJoinProgressChanged += CoinJoinProgressChanged;
		CoinJoinTracker = coinJoinTracker;
		return coinJoinTracker;
	}

	public async Task StartAsync(IWallet wallet, IWallet outputWallet, bool stopWhenAllMixed, bool overridePlebStop, CancellationToken cancellationToken)
	{
		if (overridePlebStop && !wallet.IsUnderPlebStop)
		{
			// Turn off overriding if we went above the threshold meanwhile.
			overridePlebStop = false;
			wallet.LogDebug("Do not override PlebStop anymore we are above the threshold.");
		}

		await CommandChannel.Writer.WriteAsync(new StartCoinJoinCommand(wallet, outputWallet, stopWhenAllMixed, overridePlebStop), cancellationToken).ConfigureAwait(false);
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		do
		{
			try
			{
				await WaitForStartSignalAsync(stoppingToken).ConfigureAwait(false);
				var coins = SelectCandidateCoinsAsync(Wallet);
				var shouldWalletStartCoinJoinAsync = await CoinJoinManagerHelper.ShouldWalletStartCoinJoinAsync(Wallet, !Wallet.IsAutoCoinJoin, IsOverridePlebStop, coins).ConfigureAwait(false);

				if (!shouldWalletStartCoinJoinAsync)
				{
					throw new InvalidOperationException();
				}
			}
			catch (Exception ex)
			{
				// Slowing down the loop.
				await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
			}
		}
		while (!stoppingToken.IsCancellationRequested);
	}

	private async Task WaitForStartSignalAsync(CancellationToken stoppingToken)
	{
		// Starting
		if (Wallet.IsAutoCoinJoin)
		{
			// We just start
			return;
		}
		else
		{
			// We wait for the start command
			do
			{
				var command = await CommandChannel.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
				if (command is StartCoinJoinCommand)
				{
					break;
				}
			}
			while (!stoppingToken.IsCancellationRequested);
		}
	}

	private async Task<IEnumerable<SmartCoin>> SelectCandidateCoinsAsync(IWallet walletToStart)
	{
		if (WasabiBackendStatusProvider.LastResponse is not { } synchronizerResponse)
		{
			throw new InvalidOperationException();
		}

		var bestHeight = synchronizerResponse.BestHeight;

		var coinCandidates = new CoinsView(await walletToStart.GetCoinjoinCoinCandidatesAsync().ConfigureAwait(false))
			.Available()
			.Where(x => !CoinRefrigerator.IsFrozen(x))
			.ToArray();

		// If there is no available coin candidates, then don't mix.
		if (coinCandidates.Length == 0)
		{
			throw new CoinJoinClientException(CoinjoinError.NoCoinsEligibleToMix, "No candidate coins available to mix.");
		}

		var bannedCoins = coinCandidates.Where(x => CoinPrison.TryGetOrRemoveBannedCoin(x, out _)).ToArray();
		var immatureCoins = coinCandidates.Where(x => x.Transaction.IsImmature(bestHeight)).ToArray();
		var unconfirmedCoins = coinCandidates.Where(x => !x.Confirmed).ToArray();
		var excludedCoins = coinCandidates.Where(x => x.IsExcludedFromCoinJoin).ToArray();

		coinCandidates = coinCandidates
			.Except(bannedCoins)
			.Except(immatureCoins)
			.Except(unconfirmedCoins)
			.Except(excludedCoins)
			.ToArray();

		if (coinCandidates.Length == 0)
		{
			var anyNonPrivateUnconfirmed = unconfirmedCoins.Any(x => !x.IsPrivate(walletToStart.AnonScoreTarget));
			var anyNonPrivateImmature = immatureCoins.Any(x => !x.IsPrivate(walletToStart.AnonScoreTarget));
			var anyNonPrivateBanned = bannedCoins.Any(x => !x.IsPrivate(walletToStart.AnonScoreTarget));
			var anyNonPrivateExcluded = excludedCoins.Any(x => !x.IsPrivate(walletToStart.AnonScoreTarget));

			var errorMessage = $"Coin candidates are empty! {nameof(anyNonPrivateUnconfirmed)}:{anyNonPrivateUnconfirmed} {nameof(anyNonPrivateImmature)}:{anyNonPrivateImmature} {nameof(anyNonPrivateBanned)}:{anyNonPrivateBanned} {nameof(anyNonPrivateExcluded)}:{anyNonPrivateExcluded}";

			if (anyNonPrivateUnconfirmed)
			{
				throw new CoinJoinClientException(CoinjoinError.NoConfirmedCoinsEligibleToMix, errorMessage);
			}

			if (anyNonPrivateImmature)
			{
				throw new CoinJoinClientException(CoinjoinError.OnlyImmatureCoinsAvailable, errorMessage);
			}

			if (anyNonPrivateBanned)
			{
				throw new CoinJoinClientException(CoinjoinError.CoinsRejected, errorMessage);
			}

			if (anyNonPrivateExcluded)
			{
				throw new CoinJoinClientException(CoinjoinError.OnlyExcludedCoinsAvailable, errorMessage);
			}
		}

		return coinCandidates;
	}

	private record CoinJoinCommand(IWallet Wallet);
	private record StartCoinJoinCommand(IWallet Wallet, IWallet OutputWallet, bool StopWhenAllMixed, bool OverridePlebStop) : CoinJoinCommand(Wallet);
}
