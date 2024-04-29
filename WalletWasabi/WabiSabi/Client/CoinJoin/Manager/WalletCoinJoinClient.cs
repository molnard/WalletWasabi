using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

	protected override Task ExecuteAsync(CancellationToken stoppingToken)
	{
		throw new NotImplementedException();
	}
}
