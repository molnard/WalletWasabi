using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using NBitcoin;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Backend.Models;
using WalletWasabi.Blockchain.Analysis.FeesEstimation;
using WalletWasabi.Blockchain.BlockFilters;
using WalletWasabi.Blockchain.Blocks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Mempool;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Stores;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Wallets;
using WalletWasabi.WebClients.Wasabi;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallet;

public class WalletSynchronizationTests
{
	[Fact]
	public async Task WalletTurboSyncTest2Async()
	{
		var network = Network.RegTest;
		FeeRate feeRate = FeeRate.Zero;
		var rpc = new MockRpcClient();

		Dictionary<uint256, Block> blockChain = new();

		Block CreateBlock(BitcoinAddress address, IEnumerable<Transaction>? transactions = null)
		{
			Block block = network.Consensus.ConsensusFactory.CreateBlock();
			block.Header.HashPrevBlock = blockChain.Keys.LastOrDefault() ?? uint256.Zero;
			var coinBaseTransaction = Transaction.Create(network);

			var amount = Money.Coins(5) + Money.Satoshis(blockChain.Count); // Add block height to make sure the coinbase tx hash differs.
			coinBaseTransaction.Outputs.Add(amount, address);
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
			CreateBlock(minerDestination.ScriptPubKey.GetDestinationAddress(network)!, new[] { tx });
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

		KeyManager keyManager = KeyManager.CreateNewWatchOnly(wallet.ExtKey.Neuter(), null!);
		var keys = keyManager.GetKeys(k => true); //Make sure keys are asserted.

		Assert.Contains(keys.Where(key => key.IsInternal), key => key.P2wpkhScript == destination.ScriptPubKey);

		var dir = Common.GetWorkDir("WalletSynchronizationTests", "WalletTurboSyncTest2Async");

		File.Delete(Path.Combine(dir, "IndexStore.sqlite")); //Make sure to start with an empty DB
		await using var indexStore = new IndexStore(Path.Combine(dir, "indexStore"), network, new SmartHeaderChain());

		await using var transactionStore = new AllTransactionStore(Path.Combine(dir, "transactionStore"), network);
		var mempoolService = new MempoolService();

		var blockRepositoryMock = new Mock<IRepository<uint256, Block>>();
		blockRepositoryMock
			.Setup(br => br.TryGetAsync(It.IsAny<uint256>(), It.IsAny<CancellationToken>()))
			.Returns((uint256 hash, CancellationToken _) => Task.FromResult(blockChain[hash])!);
		blockRepositoryMock
			.Setup(br => br.SaveAsync(It.IsAny<Block>(), It.IsAny<CancellationToken>()))
			.Returns((Block _, CancellationToken _) => Task.CompletedTask);

		var bitcoinStore = new BitcoinStore(indexStore, transactionStore, mempoolService, blockRepositoryMock.Object);
		await bitcoinStore.InitializeAsync(); //StartingFilter already added to IndexStore after this line.

		var filters = BuildFiltersForBlockChain(blockChain, network);
		await indexStore.AddNewFiltersAsync(filters.Skip(1));

		var serviceConfiguration = new ServiceConfiguration(new UriEndPoint(new Uri("http://www.nomatter.dontcare")), Money.Coins(WalletWasabi.Helpers.Constants.DefaultDustThreshold));
		await using HttpClientFactory httpClientFactory = new(torEndPoint: null, backendUriGetter: () => null!);
		WasabiSynchronizer synchronizer = new(requestInterval: TimeSpan.FromSeconds(3), 1000, bitcoinStore, httpClientFactory);
		HybridFeeProvider feeProvider = new(synchronizer, null);
		IRepository<uint256, Block> blockRepository = bitcoinStore.BlockRepository;
		using MemoryCache cache = new(new MemoryCacheOptions());
		await using SpecificNodeBlockProvider specificNodeBlockProvider = new(network, serviceConfiguration, null);
		SmartBlockProvider blockProvider = new(bitcoinStore.BlockRepository, rpcBlockProvider: null, null, null, cache);

		using var wallet1 = WalletWasabi.Wallets.Wallet.CreateAndRegisterServices(network, bitcoinStore, keyManager, synchronizer, dir, serviceConfiguration, feeProvider, blockProvider);

		await wallet1.PerformWalletSynchronizationAsync(SyncType.Turbo, CancellationToken.None);
		await wallet1.PerformWalletSynchronizationAsync(SyncType.NonTurbo, CancellationToken.None);

		Assert.Equal(2, wallet1.Coins.Count());
	}

	private IEnumerable<FilterModel> BuildFiltersForBlockChain(Dictionary<uint256, Block> blockChain, Network network)
	{
		Dictionary<OutPoint, Script> outPoints = blockChain.Values
			.SelectMany(block => block.Transactions)
			.SelectMany(tx => tx.Outputs.AsIndexedOutputs())
			.ToDictionary(output => new OutPoint(output.Transaction, output.N), output => output.TxOut.ScriptPubKey);

		List<FilterModel> filters = new();

		var startingFilter = StartingFilters.GetStartingFilter(network);
		filters.Add(startingFilter);

		foreach (var block in blockChain.Values)
		{
			var inputScriptPubKeys = block.Transactions
				.SelectMany(tx => tx.Inputs)
				.Where(input => outPoints.ContainsKey(input.PrevOut))
				.Select(input => outPoints[input.PrevOut]);

			var outputScriptPubKeys = block.Transactions
				.SelectMany(tx => tx.Outputs)
				.Select(output => output.ScriptPubKey);

			var scripts = inputScriptPubKeys.Union(outputScriptPubKeys);
			var entries = scripts.Any() ? scripts.Select(x => x.ToCompressedBytes()) : IndexBuilderService.DummyScript;

			var filter = new GolombRiceFilterBuilder()
				.SetKey(block.GetHash())
				.SetP(20)
				.SetM(1 << 20)
				.AddEntries(entries)
				.Build();

			var tipFilter = filters.Last();

			var smartHeader = new SmartHeader(block.GetHash(), tipFilter.Header.BlockHash, tipFilter.Header.Height + 1, DateTimeOffset.UtcNow);

			filters.Add(new FilterModel(smartHeader, filter));
		}

		return filters;
	}
}
