using NBitcoin;

namespace WalletWasabi.WabiSabi.Backend.Banning;
public record CoinVerifyInfo(Coin Coin, bool SuccessfulVerification, bool ShouldBan, CoinVerifierReason Reason, ApiResponseItem? ApiResponseItem = null, Exception? Exception = null);

public enum CoinVerifierReason
{
	Whitelisted,
	FromCoinjoin,
	RemoteApiCheck,
}
