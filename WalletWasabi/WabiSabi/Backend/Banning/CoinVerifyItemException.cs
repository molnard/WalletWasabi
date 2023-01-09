using NBitcoin;

namespace WalletWasabi.WabiSabi.Backend.Banning;

public class CoinVerifyItemException : Exception
{
	public CoinVerifyItemException(Coin coin, Exception exception)
	{
		Coin = coin;
		Exception = exception;
	}

	public Coin Coin { get; }
	public Exception Exception { get; }
}
