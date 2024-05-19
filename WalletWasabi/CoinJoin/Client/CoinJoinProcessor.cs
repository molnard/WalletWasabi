using NBitcoin;
using Nito.AsyncEx;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Backend.Models.Responses;
using WalletWasabi.BitcoinCore.Rpc;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Extensions;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Wallets;
using WalletWasabi.WebClients.Wasabi;

namespace WalletWasabi.CoinJoin.Client;

public class CoinJoinProcessor : IDisposable
{
	private volatile bool _disposedValue = false; // To detect redundant calls

	public CoinJoinProcessor(Network network, WasabiSynchronizer synchronizer, WalletManager walletManager, WasabiClient wasabiClient, IRPCClient? rpc)
	{
		Synchronizer = synchronizer;
		WalletManager = walletManager;
		Network = network;
        WasabiClient = wasabiClient;
		RpcClient = rpc;
		ProcessLock = new AsyncLock();
		Synchronizer.ResponseArrived += Synchronizer_ResponseArrivedAsync;
	}

	private WasabiSynchronizer Synchronizer { get; }
	private WalletManager WalletManager { get; }
	private Network Network { get; }
	private WasabiClient WasabiClient { get; }
	private IRPCClient? RpcClient { get; set; }
	private AsyncLock ProcessLock { get; }

	private async void Synchronizer_ResponseArrivedAsync(object? sender, SynchronizeResponse response)
	{
		try
		{
			using (await ProcessLock.LockAsync())
			{
				var unconfirmedCoinJoinHashes = response.UnconfirmedCoinJoins;
				if (!unconfirmedCoinJoinHashes.Any())
				{
					return;
				}

				var txsNotKnownByAWallet = WalletManager.FilterUnknownCoinjoins(unconfirmedCoinJoinHashes);

				var unconfirmedCoinJoins = await WasabiClient.GetTransactionsAsync(Network, txsNotKnownByAWallet, CancellationToken.None).ConfigureAwait(false);

				foreach (var tx in unconfirmedCoinJoins.Select(x => new SmartTransaction(x, Height.Mempool)))
				{
					if (RpcClient is null
						|| await TryBroadcastTransactionWithRpcAsync(tx).ConfigureAwait(false)
						|| (await RpcClient.TestAsync().ConfigureAwait(false)) is { }) // If the test throws exception then I believe it, because RPC is down and the backend is the god.
					{
						WalletManager.ProcessCoinJoin(tx);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	private async Task<bool> TryBroadcastTransactionWithRpcAsync(SmartTransaction transaction)
	{
		try
		{
			if (RpcClient is null)
			{
				throw new InvalidOperationException("RpcClient is not available");
			}
			await RpcClient.SendRawTransactionAsync(transaction.Transaction).ConfigureAwait(false);
			Logger.LogInfo($"Transaction is successfully broadcasted with RPC: {transaction.GetHash()}.");

			return true;
		}
		catch
		{
			return false;
		}
	}

	#region IDisposable Support

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposedValue)
		{
			if (disposing)
			{
				Synchronizer.ResponseArrived -= Synchronizer_ResponseArrivedAsync;
			}

			_disposedValue = true;
		}
	}

	// This code added to correctly implement the disposable pattern.
	public void Dispose()
	{
		// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
		Dispose(true);
	}

	#endregion IDisposable Support
}
