using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.WabiSabi.Client.CoinJoinProgressEvents;

public class RoundEnded : CoinJoinProgressEventArgs
{
	public RoundEnded(RoundState lastRoundState, bool successWithoutWallet = false)
	{
		LastRoundState = lastRoundState;
		SuccessWithoutWallet = successWithoutWallet;
	}

	public RoundState LastRoundState { get; }
	public bool IsStopped { get; set; }
	public bool SuccessWithoutWallet { get; }
}
