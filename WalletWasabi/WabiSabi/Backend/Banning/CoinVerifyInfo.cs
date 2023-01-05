using NBitcoin;

namespace WalletWasabi.WabiSabi.Backend.Banning;
public record CoinVerifyInfo(Coin Coin, bool SuccessfulVerification, bool ShouldBan, CoinVerifierReason Reason, ApiResponseItem? ApiResponseItem);

public enum CoinVerifierReason
{
	Whitelisted,
	FromCoinjoin,
	RemoteApiCheck,
}
