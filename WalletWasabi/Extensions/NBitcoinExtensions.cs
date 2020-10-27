using NBitcoin.DataEncoders;
using NBitcoin.Protocol;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.CoinJoin.Common.Crypto;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using static WalletWasabi.Crypto.SchnorrBlinding;

namespace NBitcoin
{
	public static class NBitcoinExtensions
	{
		public static async Task<Block> DownloadBlockAsync(this Node node, uint256 hash, CancellationToken cancellationToken)
		{
			if (node.State == NodeState.Connected)
			{
				node.VersionHandshake(cancellationToken);
			}

			using var listener = node.CreateListener();
			var getdata = new GetDataPayload(new InventoryVector(node.AddSupportedOptions(InventoryType.MSG_BLOCK), hash));
			await node.SendMessageAsync(getdata).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();

			// Bitcoin Core processes the messages sequentially and does not send a NOTFOUND message if the remote node is pruned and the data not available.
			// A good way to get any feedback about whether the node knows the block or not is to send a ping request.
			// If block is not known by the remote node, the pong will be sent immediately, else it will be sent after the block download.
			ulong pingNonce = RandomUtils.GetUInt64();
			await node.SendMessageAsync(new PingPayload() { Nonce = pingNonce }).ConfigureAwait(false);
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var message = listener.ReceiveMessage(cancellationToken);
				if (message.Message.Payload is NotFoundPayload ||
					(message.Message.Payload is PongPayload p && p.Nonce == pingNonce))
				{
					throw new InvalidOperationException($"Disconnected local node, because it does not have the block data.");
				}
				else if (message.Message.Payload is BlockPayload b && b.Object?.GetHash() == hash)
				{
					return b.Object;
				}
			}
		}

		public static IEnumerable<OutPoint> ToOutPoints(this TxInList me)
		{
			foreach (var input in me)
			{
				yield return input.PrevOut;
			}
		}

		public static IEnumerable<Coin> GetCoins(this TxOutList me, Script script)
		{
			return me.AsCoins().Where(c => c.ScriptPubKey == script);
		}

		public static string ToHex(this IBitcoinSerializable me)
		{
			return ByteHelpers.ToHex(me.ToBytes());
		}

		public static void FromHex(this IBitcoinSerializable me, string hex)
		{
			Guard.NotNullOrEmptyOrWhitespace(nameof(hex), hex);
			me.FromBytes(ByteHelpers.FromHex(hex));
		}

		/// <summary>
		/// Based on transaction data, it decides if it's possible that native segwit script played a par in this transaction.
		/// </summary>
		public static bool PossiblyP2WPKHInvolved(this Transaction me)
		{
			// We omit Guard, because it's performance critical in Wasabi.
			// We start with the inputs, because, this check is faster.
			// Note: by testing performance the order does not seem to affect the speed of loading the wallet.
			foreach (TxIn input in me.Inputs)
			{
				if (input.ScriptSig is null || input.ScriptSig == Script.Empty)
				{
					return true;
				}
			}
			foreach (TxOut output in me.Outputs)
			{
				if (output.ScriptPubKey.IsScriptType(ScriptType.P2WPKH))
				{
					return true;
				}
			}
			return false;
		}

		public static bool HasIndistinguishableOutputs(this Transaction me)
		{
			var hashset = new HashSet<long>();
			foreach (var name in me.Outputs.Select(x => x.Value))
			{
				if (!hashset.Add(name))
				{
					return true;
				}
			}
			return false;
		}

		public static IEnumerable<(Money value, int count)> GetIndistinguishableOutputs(this Transaction me, bool includeSingle)
		{
			return me.Outputs.GroupBy(x => x.Value)
				.ToDictionary(x => x.Key, y => y.Count())
				.Select(x => (x.Key, x.Value))
				.Where(x => includeSingle || x.Value > 1);
		}

		public static int GetAnonymitySet(this Transaction me, int outputIndex)
		{
			// 1. Get the output corresponting to the output index.
			var output = me.Outputs[outputIndex];
			// 2. Get the number of equal outputs.
			int equalOutputs = me.GetIndistinguishableOutputs(includeSingle: true).Single(x => x.value == output.Value).count;
			// 3. Anonymity set cannot be larger than the number of inputs.
			var inputCount = me.Inputs.Count;
			var anonSet = Math.Min(equalOutputs, inputCount);
			return anonSet;
		}

		public static int GetAnonymitySet(this Transaction me, uint outputIndex) => GetAnonymitySet(me, (int)outputIndex);

		public static int GetAnonymitySet(this Transaction me, int outputIndex, ICoinsView allWalletCoins)
		{
			var spentOwnCoins = allWalletCoins.OutPoints(me.Inputs.Select(x => x.PrevOut)).ToList();
			var numberOfOwnInputs = spentOwnCoins.Count();
			// If it's a normal tx that isn't self spent, nor a coinjoin, then anonymity should stripped if there was any and start from zero.
			// Note: this is only a good idea from WWII, with WWI we calculate anonsets from the point the coin first hit the wallet.
			// Note: a bit optimization to calculate this would be to actually use own output data, but that's a bit harder to get and this'll do it:
			// If all our inputs are ours and there are more than 1 outputs then it's not a self-spent and it's not a coinjoin.
			// This'll work, because we are only calculating anonset for our own coins and Wasabi doesn't generate tx that has more than one own outputs.
			if (numberOfOwnInputs == me.Inputs.Count && me.Outputs.Count > 1)
			{
				return 1;
			}

			// Get the anonymity set of i-th output in the transaction.
			var anonset = me.GetAnonymitySet(outputIndex);
			// If we provided inputs to the transaction.
			if (numberOfOwnInputs > 0)
			{
				// Take the input that we provided with the smallest anonset.
				// Our smallest anonset input is the relevant here, because this way the common input ownership heuristic is considered.
				var smallestInputAnon = spentOwnCoins.Min(x => x.AnonymitySet);

				// Punish consolidation exponentially.
				// If there is only a single input then the exponent should be zero to divide by 1 thus retain the input coin anonset.
				var consolidatePenalty = Math.Pow(2, numberOfOwnInputs - 1);
				var privacyBonus = smallestInputAnon / consolidatePenalty;

				// If the privacy bonus is <=1 then we are not inheriting any privacy from the inputs.
				var normalizedBonus = privacyBonus - 1;
				int sanityCheckedEstimation = (int)Math.Max(0d, normalizedBonus);

				// And add that to the base anonset from the tx.
				anonset += sanityCheckedEstimation;
			}

			// Factor in script reuse.
			var output = me.Outputs[outputIndex];
			foreach (var coin in allWalletCoins.FilterBy(x => x.ScriptPubKey == output.ScriptPubKey))
			{
				anonset = Math.Min(anonset, coin.AnonymitySet);
				coin.AnonymitySet = anonset;
			}

			return anonset;
		}

		public static int GetAnonymitySet(this Transaction me, uint outputIndex, ICoinsView allWalletCoins) => GetAnonymitySet(me, (int)outputIndex, allWalletCoins);

		/// <summary>
		/// Careful, if it's in a legacy block then this won't work.
		/// </summary>
		public static bool HasWitScript(this TxIn me)
		{
			Guard.NotNull(nameof(me), me);

			bool notNull = me.WitScript is { };
			bool notEmpty = me.WitScript != WitScript.Empty;
			return notNull && notEmpty;
		}

		public static Money Percentage(this Money me, decimal perc)
		{
			return Money.Satoshis((me.Satoshi / 100m) * perc);
		}

		public static decimal ToUsd(this Money me, decimal btcExchangeRate)
		{
			return me.ToDecimal(MoneyUnit.BTC) * btcExchangeRate;
		}

		public static bool VerifyMessage(this BitcoinWitPubKeyAddress address, uint256 messageHash, byte[] signature)
		{
			PubKey pubKey = PubKey.RecoverCompact(messageHash, signature);
			return pubKey.WitHash == address.Hash;
		}

		/// <summary>
		/// If scriptpubkey is already present, just add the value.
		/// </summary>
		public static void AddWithOptimize(this TxOutList me, Money money, Script scriptPubKey)
		{
			TxOut found = me.FirstOrDefault(x => x.ScriptPubKey == scriptPubKey);
			if (found is { })
			{
				found.Value += money;
			}
			else
			{
				me.Add(money, scriptPubKey);
			}
		}

		/// <summary>
		/// If scriptpubkey is already present, just add the value.
		/// </summary>
		public static void AddWithOptimize(this TxOutList me, Money money, IDestination destination)
		{
			me.AddWithOptimize(money, destination.ScriptPubKey);
		}

		/// <summary>
		/// If scriptpubkey is already present, just add the value.
		/// </summary>
		public static void AddWithOptimize(this TxOutList me, TxOut txOut)
		{
			me.AddWithOptimize(txOut.Value, txOut.ScriptPubKey);
		}

		/// <summary>
		/// If scriptpubkey is already present, just add the value.
		/// </summary>
		public static void AddRangeWithOptimize(this TxOutList me, IEnumerable<TxOut> collection)
		{
			foreach (var txOut in collection)
			{
				me.AddWithOptimize(txOut);
			}
		}

		public static uint256 BlindMessage(this Requester requester, uint256 messageHash, SchnorrPubKey schnorrPubKey) => requester.BlindMessage(messageHash, schnorrPubKey.RpubKey, schnorrPubKey.SignerPubKey);

		public static string ToZpub(this ExtPubKey extPubKey, Network network)
		{
			var data = extPubKey.ToBytes();
			var version = (network == Network.Main)
				? new byte[] { (0x04), (0xB2), (0x47), (0x46) }
				: new byte[] { (0x04), (0x5F), (0x1C), (0xF6) };

			return Encoders.Base58Check.EncodeData(version.Concat(data).ToArray());
		}

		public static string ToZPrv(this ExtKey extKey, Network network)
		{
			var data = extKey.ToBytes();
			var version = (network == Network.Main)
				? new byte[] { (0x04), (0xB2), (0x43), (0x0C) }
				: new byte[] { (0x04), (0x5F), (0x18), (0xBC) };

			return Encoders.Base58Check.EncodeData(version.Concat(data).ToArray());
		}

		public static SmartTransaction ExtractSmartTransaction(this PSBT psbt)
		{
			var extractedTx = psbt.ExtractTransaction();
			return new SmartTransaction(extractedTx, Height.Unknown);
		}

		public static SmartTransaction ExtractSmartTransaction(this PSBT psbt, SmartTransaction unsignedSmartTransaction)
		{
			var extractedTx = psbt.ExtractTransaction();
			return new SmartTransaction(extractedTx,
				unsignedSmartTransaction.Height,
				unsignedSmartTransaction.BlockHash,
				unsignedSmartTransaction.BlockIndex,
				unsignedSmartTransaction.Label,
				unsignedSmartTransaction.IsReplacement,
				unsignedSmartTransaction.FirstSeen);
		}

		public static void SortByAmount(this TxOutList list)
		{
			list.Sort((x, y) => x.Value.CompareTo(y.Value));
		}

		/// <param name="startWithM">The keypath will start with m/ or not.</param>
		/// <param name="format">h or ', eg.: m/84h/0h/0 or m/84'/0'/0</param>
		public static string ToString(this KeyPath me, bool startWithM, string format)
		{
			var toStringBuilder = new StringBuilder(me.ToString());

			if (startWithM)
			{
				toStringBuilder.Insert(0, "m/");
			}

			if (format == "h")
			{
				toStringBuilder.Replace('\'', 'h');
			}

			return toStringBuilder.ToString();
		}

		public static BitcoinWitPubKeyAddress TransformToNetworkNetwork(this BitcoinWitPubKeyAddress me, Network desiredNetwork)
		{
			Network originalNetwork = me.Network;

			if (originalNetwork == desiredNetwork)
			{
				return me;
			}

			var newAddress = new BitcoinWitPubKeyAddress(me.Hash, desiredNetwork);

			return newAddress;
		}

		public static void SortByAmount(this TxInList list, List<Coin> coins)
		{
			var map = new Dictionary<TxIn, Coin>();
			foreach (var coin in coins)
			{
				map.Add(list.Single(x => x.PrevOut == coin.Outpoint), coin);
			}
			list.Sort((x, y) => map[x].Amount.CompareTo(map[y].Amount));
		}

		public static Money GetTotalFee(this FeeRate me, int vsize)
		{
			return Money.Satoshis(Math.Round(me.SatoshiPerByte * vsize));
		}

		public static IEnumerable<TransactionDependencyNode> ToDependencyGraph(this IEnumerable<Transaction> txs)
		{
			var lookup = new Dictionary<uint256, TransactionDependencyNode>();
			foreach (var tx in txs)
			{
				lookup.Add(tx.GetHash(), new TransactionDependencyNode { Transaction = tx });
			}

			foreach (var node in lookup.Values)
			{
				foreach (var input in node.Transaction.Inputs)
				{
					if (lookup.TryGetValue(input.PrevOut.Hash, out var parent))
					{
						if (!node.Parents.Contains(parent))
						{
							node.Parents.Add(parent);
						}
						if (!parent.Children.Contains(node))
						{
							parent.Children.Add(node);
						}
					}
				}
			}
			var nodes = lookup.Values;
			return nodes.Where(x => !x.Parents.Any());
		}

		public static IEnumerable<Transaction> OrderByDependency(this IEnumerable<TransactionDependencyNode> roots)
		{
			var parentCounter = new Dictionary<TransactionDependencyNode, int>();

			void Walk(TransactionDependencyNode node)
			{
				if (!parentCounter.ContainsKey(node))
				{
					parentCounter.Add(node, node.Parents.Count());
					foreach (var child in node.Children)
					{
						Walk(child);
					}
				}
			}

			foreach (var root in roots)
			{
				Walk(root);
			}

			var nodes = parentCounter.Where(x => x.Value == 0).Select(x => x.Key).Distinct().ToArray();
			while (nodes.Any())
			{
				foreach (var node in nodes)
				{
					yield return node.Transaction;
					parentCounter.Remove(node);
					foreach (var child in node.Children)
					{
						parentCounter[child] = parentCounter[child] - 1;
					}
				}
				nodes = parentCounter.Where(x => x.Value == 0).Select(x => x.Key).Distinct().ToArray();
			}
		}

		public static ScriptPubKeyType? GetInputScriptPubKeyType(this PSBTInput i)
		{
			if (i.WitnessUtxo.ScriptPubKey.IsScriptType(ScriptType.P2WPKH))
			{
				return ScriptPubKeyType.Segwit;
			}

			if (i.WitnessUtxo.ScriptPubKey.IsScriptType(ScriptType.P2SH) &&
				i.FinalScriptWitness.ToScript().IsScriptType(ScriptType.P2WPKH))
			{
				return ScriptPubKeyType.SegwitP2SH;
			}

			return null;
		}

		private static NumberFormatInfo CurrencyNumberFormat = new NumberFormatInfo()
		{
			NumberGroupSeparator = " ",
			NumberDecimalDigits = 0
		};

		private static string ToCurrency(this Money btc, string currency, decimal exchangeRate, bool privacyMode = false)
		{
			var dollars = exchangeRate * btc.ToDecimal(MoneyUnit.BTC);

			return privacyMode
				? $"### {currency}"
				: exchangeRate == default
					? $"??? {currency}"
					: $"{dollars.ToString("N", CurrencyNumberFormat)} {currency}";
		}

		public static string ToUsdString(this Money btc, decimal usdExchangeRate, bool privacyMode = false)
		{
			return ToCurrency(btc, "USD", usdExchangeRate, privacyMode);
		}

		/// <summary>
		/// Tries to equip the PSBT with input and output keypaths on best effort.
		/// </summary>
		public static void AddKeyPaths(this PSBT psbt, KeyManager keyManager)
		{
			if (keyManager.MasterFingerprint.HasValue)
			{
				var fp = keyManager.MasterFingerprint.Value;
				// Add input keypaths.
				foreach (var script in psbt.Inputs.Select(x => x.WitnessUtxo?.ScriptPubKey).ToArray())
				{
					if (script is { })
					{
						var hdPubKey = keyManager.GetKeyForScriptPubKey(script);
						if (hdPubKey is { })
						{
							psbt.AddKeyPath(fp, hdPubKey, script);
						}
					}
				}

				// Add output keypaths.
				foreach (var script in psbt.Outputs.Select(x => x.ScriptPubKey).ToArray())
				{
					var hdPubKey = keyManager.GetKeyForScriptPubKey(script);
					if (hdPubKey is { })
					{
						psbt.AddKeyPath(fp, hdPubKey, script);
					}
				}
			}
		}

		public static void AddKeyPath(this PSBT psbt, HDFingerprint fp, HdPubKey hdPubKey, Script script)
		{
			var rootKeyPath = new RootedKeyPath(fp, hdPubKey.FullKeyPath);
			psbt.AddKeyPath(hdPubKey.PubKey, rootKeyPath, script);
		}

		/// <summary>
		/// Tries to equip the PSBT with previous transactions with best effort.
		/// </summary>
		public static void AddPrevTxs(this PSBT psbt, AllTransactionStore transactionStore)
		{
			// Fill out previous transactions.
			foreach (var psbtInput in psbt.Inputs)
			{
				if (transactionStore.TryGetTransaction(psbtInput.PrevOut.Hash, out var tx))
				{
					psbtInput.NonWitnessUtxo = tx.Transaction;
				}
				else
				{
					Logger.LogInfo($"Transaction id: {psbtInput.PrevOut.Hash} is missing from the {nameof(transactionStore)}. Ignoring...");
				}
			}
		}
	}
}
