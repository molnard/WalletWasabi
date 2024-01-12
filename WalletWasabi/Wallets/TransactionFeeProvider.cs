using Microsoft.Extensions.Hosting;
using NBitcoin;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Logging;
using WalletWasabi.Tor.Http;
using WalletWasabi.Tor.Http.Extensions;
using WalletWasabi.WebClients.Wasabi;

namespace WalletWasabi.Wallets;

public class TransactionFeeProvider : BackgroundService
{
	private const int MaximumDelayInSeconds = 120;
	private const int MaximumRequestsInParallel = 3;

	public TransactionFeeProvider(WasabiHttpClientFactory httpClientFactory)
	{
		HttpClient = httpClientFactory.NewHttpClient(httpClientFactory.BackendUriGetter, Tor.Socks5.Pool.Circuits.Mode.NewCircuitPerRequest);
	}

	public event EventHandler<EventArgs>? RequestedFeeArrived;

	public ConcurrentDictionary<uint256, Money> FeeCache { get; } = new();
	public Channel<uint256> TransactionIdChannel { get; } = Channel.CreateUnbounded<uint256>();
	private IHttpClient HttpClient { get; }

	private async Task FetchTransactionFeeAsync(uint256 txid, CancellationToken cancellationToken)
	{
		const int MaxAttempts = 3;

		for (int i = 0; i < MaxAttempts; i++)
		{
			try
			{
				using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(20));
				using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

				var response = await HttpClient.SendAsync(
					HttpMethod.Get,
					$"api/v{Helpers.Constants.BackendMajorVersion}/btc/Blockchain/get-transaction-fee?transactionId={txid}",
					null,
					linkedCts.Token).ConfigureAwait(false);

				if (response.StatusCode != HttpStatusCode.OK)
				{
					await response.ThrowRequestExceptionFromContentAsync(cancellationToken).ConfigureAwait(false);
				}

				Money fee = await response.Content.ReadAsJsonAsync<Money>().ConfigureAwait(false);

				if (!FeeCache.TryAdd(txid, fee))
				{
					throw new InvalidOperationException($"Failed to cache {txid} with fee: {fee}");
				}

				RequestedFeeArrived?.Invoke(this, EventArgs.Empty);
				return;
			}
			catch (Exception ex)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					return;
				}

				Logger.LogWarning($"Attempt: {i}. Failed to fetch transaction fee. {ex}");
			}
		}
	}

	public bool TryGetFeeFromCache(uint256 txid, [NotNullWhen(true)] out Money? fee)
	{
		return FeeCache.TryGetValue(txid, out fee);
	}

	public void BeginRequestTransactionFee(SmartTransaction tx)
	{
		if (!tx.Confirmed && tx.ForeignInputs.Count != 0)
		{
			if (!TransactionIdChannel.Writer.TryWrite(tx.GetHash()))
			{
				Logger.LogError("Failed to write channel.");
			}
		}
	}

	protected override async Task ExecuteAsync(CancellationToken cancel)
	{
		List<Task> activeTasks = new(capacity: MaximumRequestsInParallel);

		while (!cancel.IsCancellationRequested)
		{
			if (activeTasks.Count == 0)
			{
				// We have nothing to do so just wait until we got a new request.
				await TransactionIdChannel.Reader.WaitToReadAsync(cancel).ConfigureAwait(false);
			}
			else
			{
				// Preparing cancellation for cleanup at the end.
				using CancellationTokenSource waitingCts = new();
				using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancel, waitingCts.Token);

				var waitForAnyCompletedTask = Task.Run(async () => await Task.WhenAny(activeTasks).WithCancellation(linkedCts.Token).ConfigureAwait(false), linkedCts.Token);
				var waitForRequestTask = Task.Run(async () => await TransactionIdChannel.Reader.WaitToReadAsync(linkedCts.Token).ConfigureAwait(false), linkedCts.Token);

				// Wait if we got a new request or any task completed.
				await Task.WhenAny(waitForRequestTask, waitForAnyCompletedTask).ConfigureAwait(false);

				// Cleanup pending tasks
				linkedCts.Cancel();
			}

			// While we have capacity, read the channel and add new tasks. Otherwise items wait in the channel.
			if (activeTasks.Count < MaximumRequestsInParallel && TransactionIdChannel.Reader.TryPeek(out var _))
			{
				List<uint256> todo = [];
				await foreach (var txId in TransactionIdChannel.Reader.ReadAllAsync(cancel))
				{
					todo.Add(txId);
				}

				foreach (var txId in todo.OrderBy(t => t.ToString()))
				{
					// Start the task and add.
					var task = Task.Run(async () => await ScheduledTask(txId).ConfigureAwait(false), cancel);
					activeTasks.Add(task);
				}
			}

			// Check if something is completed.
			while (activeTasks.FirstOrDefault(t => t.IsCompleted) is { } completedTask)
			{
				activeTasks.Remove(completedTask);
				try
				{
					// We can handle stuff here synchronously. Like invoke the event or hadle exceptions whatever.
					await completedTask.ConfigureAwait(false);
				}
				catch (Exception)
				{
					// Do nothing.
				}
			}
		}

		async Task ScheduledTask(uint256 txid)
		{
			var random = new Random();
			var delayInSeconds = random.Next(MaximumDelayInSeconds);
			var delay = TimeSpan.FromSeconds(delayInSeconds);

			await Task.Delay(delay, cancel).ConfigureAwait(false);

			await FetchTransactionFeeAsync(txid, cancel).ConfigureAwait(false);
		}
	}
}
