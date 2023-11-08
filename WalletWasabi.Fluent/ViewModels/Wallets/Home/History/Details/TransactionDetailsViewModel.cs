using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using NBitcoin;
using ReactiveUI;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Models;
using WalletWasabi.Fluent.Helpers;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Home.History.Details;

[NavigationMetaData(Title = "Transaction Details")]
public partial class TransactionDetailsViewModel : RoutableViewModel
{
	private readonly IWalletModel _wallet;

	[AutoNotify] private bool _isConfirmed;
	[AutoNotify] private string? _amountText = "";
	[AutoNotify] private string? _blockHash;
	[AutoNotify] private int _blockHeight;
	[AutoNotify] private int _confirmations;
	[AutoNotify] private TimeSpan? _confirmationTime;
	[AutoNotify] private string? _dateString;
	[AutoNotify] private bool _isConfirmationTimeVisible;
	[AutoNotify] private bool _isLabelsVisible;
	[AutoNotify] private LabelsArray? _labels;
	[AutoNotify] private Amount? _amount;
	[AutoNotify] private Amount? _fee;
	[AutoNotify] private bool _isFeeVisible;

	public TransactionDetailsViewModel(UiContext uiContext, IWalletModel wallet, TransactionModel model)
	{
		UiContext = uiContext;
		_wallet = wallet;

		NextCommand = ReactiveCommand.Create(OnNext);
		TransactionId = model.Id;
		DestinationAddresses = wallet.Transactions.GetDestinationAddresses(model.Id).ToArray();

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: true);

		UpdateValues(model);
	}

	public uint256 TransactionId { get; }

	public ICollection<BitcoinAddress> DestinationAddresses { get; }


	private void UpdateValues(TransactionModel model)
	{
		DateString = model.DateString;
		Labels = model.Labels;
		BlockHeight = model.BlockHeight;
		Confirmations = model.Confirmations;

		Fee = UiContext.AmountProvider.Create(model.GetFee());
		IsFeeVisible = Fee != null && Fee.HasBalance;

		var confirmationTime = _wallet.Transactions.TryEstimateConfirmationTime(model);
		if (confirmationTime is { })
		{
			ConfirmationTime = estimate;
		}

		IsConfirmed = Confirmations > 0;

		if (model.Amount < Money.Zero)
		{
			Amount = _wallet.AmountProvider.Create(-model.Amount - (model.Fee ?? Money.Zero));
			AmountText = "Amount sent";
		}
		else
		{
			Amount = _wallet.AmountProvider.Create(model.Amount);
			AmountText = "Amount received";
		}

		BlockHash = model.BlockHash?.ToString();

		IsConfirmationTimeVisible = ConfirmationTime.HasValue && ConfirmationTime != TimeSpan.Zero;
		IsLabelsVisible = Labels.HasValue && Labels.Value.Any();
	}

	private void OnNext()
	{
		Navigate().Clear();
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		_wallet.Transactions.Cache
			                .Connect()
							.Do(_ => UpdateCurrentTransaction())
							.Subscribe()
							.DisposeWith(disposables);
	}

	private void UpdateCurrentTransaction()
	{
		if (_wallet.Transactions.TryGetById(TransactionId, false, out var transaction))
		{
			UpdateValues(transaction);
		}
	}
}
