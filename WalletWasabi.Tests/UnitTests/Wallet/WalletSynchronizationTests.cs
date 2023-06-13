using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallet;

public class WalletSynchronizationTests
{
	[Fact]
	public async Task WalletTurboSyncTest2Async()
	{
		FeeRate feeRate = FeeRate.Zero;
		var rpc = new MockRpcClient();

		Dictionary<uint256, Block> blockChain = new();

		Block CreateBlock(BitcoinAddress address, IEnumerable<Transaction>? transactions = null)
		{
			Block block = Network.Main.Consensus.ConsensusFactory.CreateBlock();
			block.Header.HashPrevBlock = blockChain.Keys.LastOrDefault() ?? uint256.Zero;
			var coinBaseTransaction = Transaction.Create(Network.Main);
			coinBaseTransaction.Outputs.Add(Money.Coins(5), address);
			block.AddTransaction(coinBaseTransaction);

			if (transactions is not null)
			{
				foreach (var tx in transactions)
				{
					block.AddTransaction(tx);
				}
			}

			blockChain.Add(block.GetHash(), block);
			return block;
		}

		rpc.OnGenerateToAddressAsync = (blockCount, address) => Task.FromResult(
			Enumerable
				.Range(0, blockCount)
				.Select(_ => CreateBlock(address))
				.Select(block => block.GetHash())
				.ToArray());

		rpc.OnGetBlockAsync = (blockHash) => Task.FromResult(blockChain[blockHash]);

		rpc.OnGetRawTransactionAsync = (txHash, _) => Task.FromResult(
			blockChain.Values.SelectMany(block => block.Transactions).First(tx => tx.GetHash() == txHash));

		var minerWallet = new TestWallet("MinerWallet", rpc);
		await minerWallet.GenerateAsync(101, CancellationToken.None);
		var minerDestination = minerWallet.GetNextDestinations(1, false).Single();

		rpc.OnSendRawTransactionAsync = (tx) =>
		{
			CreateBlock(minerDestination.ScriptPubKey.GetDestinationAddress(Network.Main)!, new[] { tx });
			return tx.GetHash();
		};

		var wallet = new TestWallet("wallet", rpc);
		var destination = wallet.GetNextInternalDestinations(1).Single();

		var txId = await minerWallet.SendToAsync(Money.Coins(1), destination.ScriptPubKey, feeRate, CancellationToken.None);
		var tx = await rpc.GetRawTransactionAsync(txId);
		wallet.ScanTransaction(tx);

		await wallet.SendToAsync(Money.Coins(1), minerDestination.ScriptPubKey, feeRate, CancellationToken.None);

		// Address re-use.
		var txId2 = await minerWallet.SendToAsync(Money.Coins(1), destination.ScriptPubKey, feeRate, CancellationToken.None);
		var tx2 = await rpc.GetRawTransactionAsync(txId2);
		wallet.ScanTransaction(tx2);
	}
}
