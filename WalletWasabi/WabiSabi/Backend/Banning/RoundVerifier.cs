using NBitcoin;
using NBitcoin.RPC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WalletWasabi.WabiSabi.Backend.Models;

namespace WalletWasabi.WabiSabi.Backend.Banning;

public class RoundVerifier
{
	public RoundVerifier(uint256 roundId)
	{
		RoundId = roundId;
	}

	public bool IsFinished { get; internal set; }
	public uint256 RoundId { get; internal set; }
	public bool IsStarted { get; internal set; }

	internal void AddAlice(Alice alice, GetTxOutResponse txOutResponse)
	{
		throw new NotImplementedException();
	}

	internal void Close()
	{
		throw new NotImplementedException();
	}

	internal void Start()
	{
		throw new NotImplementedException();
	}
}
