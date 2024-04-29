using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Wallets;

namespace WalletWasabi.WabiSabi.Client.CoinJoin.Manager;

public class WalletCoinJoinClient : BackgroundService
{
	public WalletCoinJoinClient(IWallet wallet, CoinJoinTrackerFactory coinJoinTrackerFactory)
	{
		Wallet = wallet;
		CoinJoinTrackerFactory = coinJoinTrackerFactory;
	}

	private IWallet Wallet { get; }
	private CoinJoinTrackerFactory CoinJoinTrackerFactory { get; }

	public async Task<CoinJoinTracker> CreateAndStartAsync(IWallet outputWallet, Func<Task<IEnumerable<SmartCoin>>> coinCandidatesFunc, bool stopWhenAllMixed, bool overridePlebStop)
	{
		var coinJoinTracker = await CoinJoinTrackerFactory.CreateAndStartAsync(Wallet, outputWallet, coinCandidatesFunc, stopWhenAllMixed, overridePlebStop).ConfigureAwait(false);
		return coinJoinTracker;
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken)
	{
		throw new NotImplementedException();
	}
}
