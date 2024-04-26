using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Services;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.CoinJoinProgressEvents;
using WalletWasabi.Wallets;

namespace WalletWasabi.WabiSabi.Client;

public class CoinJoinTracker : BackgroundService
{
	private bool _disposedValue;

	public CoinJoinTracker(
		IWallet wallet,
		CoinJoinClient coinJoinClient,
		Func<Task<IEnumerable<SmartCoin>>> coinCandidatesFunc,
		bool stopWhenAllMixed,
		bool overridePlebStop,
		IWallet outputWallet)
	{
		Wallet = wallet;
		CoinJoinClient = coinJoinClient;
		CoinCandidatesFunc = coinCandidatesFunc;
		CoinJoinClient.CoinJoinClientProgress += CoinJoinClient_CoinJoinClientProgress;

		StopWhenAllMixed = stopWhenAllMixed;
		OverridePlebStop = overridePlebStop;
		OutputWallet = outputWallet;
	}

	public event EventHandler<CoinJoinProgressEventArgs>? WalletCoinJoinProgressChanged;

	public ImmutableList<SmartCoin> CoinsInCriticalPhase => CoinJoinClient.CoinsInCriticalPhase;
	private CoinJoinClient CoinJoinClient { get; }
	public Func<Task<IEnumerable<SmartCoin>>> CoinCandidatesFunc { get; }
	private CancellationTokenSource CancellationTokenSource { get; } = new();

	public IWallet Wallet { get; }
	public Task<CoinJoinResult> CoinJoinTask => CoinJoinTaskCompletionSource.Task;
	private TaskCompletionSource<CoinJoinResult> CoinJoinTaskCompletionSource { get; } = new TaskCompletionSource<CoinJoinResult>();

	public bool StopWhenAllMixed { get; set; }
	public bool OverridePlebStop { get; }
	public IWallet OutputWallet { get; }

	public bool IsCompleted => CoinJoinTask.IsCompleted;
	public bool InCriticalCoinJoinState { get; private set; }
	public bool IsStopped { get; set; }
	public List<CoinBanned> BannedCoins { get; private set; } = new();

	public void SignalStop()
	{
		IsStopped = true;
		if (!InCriticalCoinJoinState)
		{
			CancellationTokenSource.Cancel();
		}
	}

	private void CoinJoinClient_CoinJoinClientProgress(object? sender, CoinJoinProgressEventArgs coinJoinProgressEventArgs)
	{
		switch (coinJoinProgressEventArgs)
		{
			case EnteringCriticalPhase:
				InCriticalCoinJoinState = true;
				break;

			case LeavingCriticalPhase:
				InCriticalCoinJoinState = false;
				break;

			case RoundEnded roundEnded:
				roundEnded.IsStopped = IsStopped;
				break;

			case CoinBanned coinBanned:
				BannedCoins.Add(coinBanned);
				break;
		}

		WalletCoinJoinProgressChanged?.Invoke(Wallet, coinJoinProgressEventArgs);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposedValue)
		{
			if (disposing)
			{
				CoinJoinClient.CoinJoinClientProgress -= CoinJoinClient_CoinJoinClientProgress;
				CancellationTokenSource.Dispose();
			}

			_disposedValue = true;
		}
	}

	public override void Dispose()
	{
		base.Dispose();
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, CancellationTokenSource.Token);
			var coinJoinResult = await CoinJoinClient.StartCoinJoinAsync(CoinCandidatesFunc, StopWhenAllMixed, linkedCts.Token).ConfigureAwait(false);
			CoinJoinTaskCompletionSource.TrySetResult(coinJoinResult);
		}
		catch (Exception ex)
		{
			CoinJoinTaskCompletionSource.TrySetException(ex);
		}
	}
}
