using NBitcoin;
using Nito.AsyncEx;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Crypto;
using WalletWasabi.WabiSabi.Crypto;
using WalletWasabi.WabiSabi.Backend.Banning;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Crypto.CredentialRequesting;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using WalletWasabi.Logging;

namespace WalletWasabi.WabiSabi.Backend.Rounds;

public partial class Arena : IWabiSabiApiRequestHandler
{
	public async Task<InputRegistrationResponse> RegisterInputAsync(InputRegistrationRequest request, CancellationToken cancellationToken)
	{
		try
		{
			return await RegisterInputCoreAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (IsUserCheating(ex))
		{
			Prison.Ban(request.Input, request.RoundId);
			throw;
		}
	}

	private async Task<InputRegistrationResponse> RegisterInputCoreAsync(InputRegistrationRequest request, CancellationToken cancellationToken)
	{
		var start = DateTimeOffset.UtcNow;

		var roundState = RoundsRegistry.GetRoundState(request.RoundId, Phase.InputRegistration);

		var coin = await OutpointToCoinAsync(request, cancellationToken).ConfigureAwait(false);

		var registeredCoins = RoundsRegistry.RoundStates.Where(x => !(x.Phase == Phase.Ended && !x.WasTransactionBroadcast))
			.SelectMany(rs => rs.CoinjoinState.Inputs);

		if (registeredCoins.Any(x => x.Outpoint == coin.Outpoint))
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AliceAlreadyRegistered);
		}

		using var releaser = await RoundsRegistry.LockRoundAsync(roundState.Id).ConfigureAwait(false);
		var round = releaser.Round;
		ArenaTimeBenchmarker.AddRegisterInputLock(DateTimeOffset.UtcNow - start);

		if (round.IsInputRegistrationEnded(Config.MaxInputCountByRound))
		{
			throw new WrongPhaseException(round, Phase.InputRegistration);
		}

		if (round is BlameRound blameRound && !blameRound.BlameWhitelist.Contains(coin.Outpoint))
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.InputNotWhitelisted);
		}

		// Compute but don't commit updated coinjoin to round state, it will
		// be re-calculated on input confirmation. This is computed in here
		// for validation purposes.
		_ = round.Assert<ConstructionState>().AddInput(coin);

		var coinJoinInputCommitmentData = new CoinJoinInputCommitmentData("CoinJoinCoordinatorIdentifier", round.Id);
		if (!OwnershipProof.VerifyCoinJoinInputProof(request.OwnershipProof, coin.TxOut.ScriptPubKey, coinJoinInputCommitmentData))
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongOwnershipProof);
		}

		// Generate a new GUID with the secure random source, to be sure
		// that it is not guessable (Guid.NewGuid() documentation does
		// not say anything about GUID version or randomness source,
		// only that the probability of duplicates is very low).
		var id = new Guid(Random.GetBytes(16));

		var isPayingZeroCoordinationFee = CoinJoinIdStore.Contains(coin.Outpoint.Hash);

		if (!isPayingZeroCoordinationFee)
		{
			// If the coin comes from a tx that all of the tx inputs are coming from a CJ (1 hop - no pay).
			Transaction tx = await Rpc.GetRawTransactionAsync(coin.Outpoint.Hash, true, cancellationToken).ConfigureAwait(false);

			if (tx.Inputs.All(input => CoinJoinIdStore.Contains(input.PrevOut.Hash)))
			{
				isPayingZeroCoordinationFee = true;
			}
		}

		var alice = new Alice(coin, request.OwnershipProof, round, id, isPayingZeroCoordinationFee);

		if (alice.CalculateRemainingAmountCredentials(round.FeeRate, round.CoordinationFeeRate) <= Money.Zero)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.UneconomicalInput);
		}

		if (alice.TotalInputAmount < round.MinAmountCredentialValue)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.NotEnoughFunds);
		}
		if (alice.TotalInputAmount > round.MaxAmountCredentialValue)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.TooMuchFunds);
		}

		if (alice.TotalInputVsize > round.MaxVsizeAllocationPerAlice)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.TooMuchVsize);
		}

		var amountCredentialTask = round.AmountCredentialIssuer.HandleRequestAsync(request.ZeroAmountCredentialRequests, cancellationToken);
		var vsizeCredentialTask = round.VsizeCredentialIssuer.HandleRequestAsync(request.ZeroVsizeCredentialRequests, cancellationToken);

		if (round.RemainingInputVsizeAllocation < round.MaxVsizeAllocationPerAlice)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.VsizeQuotaExceeded);
		}

		var commitAmountCredentialResponse = await amountCredentialTask.ConfigureAwait(false);
		var commitVsizeCredentialResponse = await vsizeCredentialTask.ConfigureAwait(false);

		alice.SetDeadlineRelativeTo(round.ConnectionConfirmationTimeFrame.Duration);

		round.Alices.Add(alice);

		ArenaTimeBenchmarker.AddRegisterInput(DateTimeOffset.UtcNow - start);

		return new(alice.Id,
			commitAmountCredentialResponse,
			commitVsizeCredentialResponse,
			alice.IsPayingZeroCoordinationFee);
	}

	public async Task ReadyToSignAsync(ReadyToSignRequestRequest request, CancellationToken cancellationToken)
	{
		var start = DateTimeOffset.UtcNow;

		var roundState = RoundsRegistry.GetRoundState(request.RoundId, Phase.OutputRegistration);
		using var releaser = await RoundsRegistry.LockRoundAsync(roundState.Id).ConfigureAwait(false);
		var round = releaser.Round;
		var alice = GetAlice(request.AliceId, round);
		alice.ReadyToSign = true;

		ArenaTimeBenchmarker.AddReadyToSign(DateTimeOffset.UtcNow - start);
	}

	public async Task RemoveInputAsync(InputsRemovalRequest request, CancellationToken cancellationToken)
	{
		var roundState = RoundsRegistry.GetRoundState(request.RoundId, Phase.InputRegistration);
		using var releaser = await RoundsRegistry.LockRoundAsync(roundState.Id).ConfigureAwait(false);
		var round = releaser.Round;

		round.Alices.RemoveAll(x => x.Id == request.AliceId);
	}

	public async Task<ConnectionConfirmationResponse> ConfirmConnectionAsync(ConnectionConfirmationRequest request, CancellationToken cancellationToken)
	{
		try
		{
			return await ConfirmConnectionCoreAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (IsUserCheating(ex))
		{
			var roundState = RoundsRegistry.GetRoundState(request.RoundId);
			using var releaser = await RoundsRegistry.LockRoundAsync(roundState.Id).ConfigureAwait(false);
			var round = releaser.Round;
			var alice = GetAlice(request.AliceId, round);
			Prison.Ban(alice.Coin.Outpoint, round.Id);
			throw;
		}
	}

	private async Task<ConnectionConfirmationResponse> ConfirmConnectionCoreAsync(ConnectionConfirmationRequest request, CancellationToken cancellationToken)
	{
		var start = DateTimeOffset.UtcNow;
		Round round;
		Alice alice;
		var realAmountCredentialRequests = request.RealAmountCredentialRequests;
		var realVsizeCredentialRequests = request.RealVsizeCredentialRequests;

		var roundState = RoundsRegistry.GetRoundState(request.RoundId, Phase.InputRegistration, Phase.ConnectionConfirmation);

		using (var releaser = await RoundsRegistry.LockRoundAsync(roundState.Id).ConfigureAwait(false))
		{
			round = releaser.Round;

			alice = GetAlice(request.AliceId, round);

			if (alice.ConfirmedConnection)
			{
				Prison.Ban(alice, round.Id);
				throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AliceAlreadyConfirmedConnection, $"Round ({request.RoundId}): Alice ({request.AliceId}) already confirmed connection.");
			}

			if (realVsizeCredentialRequests.Delta != alice.CalculateRemainingVsizeCredentials(round.MaxVsizeAllocationPerAlice))
			{
				throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.IncorrectRequestedVsizeCredentials, $"Round ({request.RoundId}): Incorrect requested vsize credentials.");
			}

			var remaining = alice.CalculateRemainingAmountCredentials(round.FeeRate, round.CoordinationFeeRate);
			if (realAmountCredentialRequests.Delta != remaining)
			{
				throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.IncorrectRequestedAmountCredentials, $"Round ({request.RoundId}): Incorrect requested amount credentials.");
			}
		}

		var amountZeroCredentialTask = round.AmountCredentialIssuer.HandleRequestAsync(request.ZeroAmountCredentialRequests, cancellationToken);
		var vsizeZeroCredentialTask = round.VsizeCredentialIssuer.HandleRequestAsync(request.ZeroVsizeCredentialRequests, cancellationToken);
		Task<CredentialsResponse>? amountRealCredentialTask = null;
		Task<CredentialsResponse>? vsizeRealCredentialTask = null;

		if (round.Phase is Phase.ConnectionConfirmation)
		{
			amountRealCredentialTask = round.AmountCredentialIssuer.HandleRequestAsync(realAmountCredentialRequests, cancellationToken);
			vsizeRealCredentialTask = round.VsizeCredentialIssuer.HandleRequestAsync(realVsizeCredentialRequests, cancellationToken);
		}

		using (var releaser = await RoundsRegistry.LockRoundAsync(roundState.Id).ConfigureAwait(false))
		{
			alice = GetAlice(request.AliceId, round);

			switch (round.Phase)
			{
				case Phase.InputRegistration:
					{
						var commitAmountZeroCredentialResponse = await amountZeroCredentialTask.ConfigureAwait(false);
						var commitVsizeZeroCredentialResponse = await vsizeZeroCredentialTask.ConfigureAwait(false);
						alice.SetDeadlineRelativeTo(round.ConnectionConfirmationTimeFrame.Duration);

						ArenaTimeBenchmarker.AddConfirmConnectionZero(DateTimeOffset.UtcNow - start);

						return new(
							commitAmountZeroCredentialResponse,
							commitVsizeZeroCredentialResponse);
					}

				case Phase.ConnectionConfirmation:
					{
						// If the phase was InputRegistration before then we did not pre-calculate real credentials.
						amountRealCredentialTask ??= round.AmountCredentialIssuer.HandleRequestAsync(realAmountCredentialRequests, cancellationToken);
						vsizeRealCredentialTask ??= round.VsizeCredentialIssuer.HandleRequestAsync(realVsizeCredentialRequests, cancellationToken);

						ConnectionConfirmationResponse response = new(
							await amountZeroCredentialTask.ConfigureAwait(false),
							await vsizeZeroCredentialTask.ConfigureAwait(false),
							await amountRealCredentialTask.ConfigureAwait(false),
							await vsizeRealCredentialTask.ConfigureAwait(false));

						// Update the coinjoin state, adding the confirmed input.
						round.CoinjoinState = round.Assert<ConstructionState>().AddInput(alice.Coin);
						alice.ConfirmedConnection = true;

						ArenaTimeBenchmarker.AddConfirmConnectionReal(DateTimeOffset.UtcNow - start);

						return response;
					}

				default:
					throw new WrongPhaseException(round, Phase.InputRegistration, Phase.ConnectionConfirmation);
			}
		}
	}

	public Task RegisterOutputAsync(OutputRegistrationRequest request, CancellationToken cancellationToken)
	{
		return RegisterOutputCoreAsync(request, cancellationToken);
	}

	public async Task<EmptyResponse> RegisterOutputCoreAsync(OutputRegistrationRequest request, CancellationToken cancellationToken)
	{
		var start = DateTimeOffset.UtcNow;

		var roundState = RoundsRegistry.GetRoundState(request.RoundId, Phase.OutputRegistration);
		using var releaser = await RoundsRegistry.LockRoundAsync(roundState.Id).ConfigureAwait(false);
		var round = releaser.Round;

		ArenaTimeBenchmarker.AddRegisterOutputLock(DateTimeOffset.UtcNow - start);

		var credentialAmount = -request.AmountCredentialRequests.Delta;

		if (CoinJoinScriptStore?.Contains(request.Script) is true)
		{
			Logger.LogWarning($"Round ({request.RoundId}): Already registered script.");
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AlreadyRegisteredScript, $"Round ({request.RoundId}): Already registered script.");
		}

		var inputScripts = round.Alices.Select(a => a.Coin.ScriptPubKey).ToHashSet();
		if (inputScripts.Contains(request.Script))
		{
			Logger.LogWarning($"Round ({request.RoundId}): Already registered script in the round.");
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AlreadyRegisteredScript, $"Round ({request.RoundId}): Already registered script in round.");
		}

		Bob bob = new(request.Script, credentialAmount);

		var outputValue = bob.CalculateOutputAmount(round.FeeRate);

		var vsizeCredentialRequests = request.VsizeCredentialRequests;
		if (-vsizeCredentialRequests.Delta != bob.OutputVsize)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.IncorrectRequestedVsizeCredentials, $"Round ({request.RoundId}): Incorrect requested vsize credentials.");
		}

		// Update the current round state with the additional output to ensure it's valid.
		var newState = round.AddOutput(new TxOut(outputValue, bob.Script));

		// Verify the credential requests and prepare their responses.
		await round.AmountCredentialIssuer.HandleRequestAsync(request.AmountCredentialRequests, cancellationToken).ConfigureAwait(false);
		await round.VsizeCredentialIssuer.HandleRequestAsync(vsizeCredentialRequests, cancellationToken).ConfigureAwait(false);

		// Update round state.
		round.Bobs.Add(bob);
		round.CoinjoinState = newState;

		ArenaTimeBenchmarker.AddRegisterOutput(DateTimeOffset.UtcNow - start);

		return EmptyResponse.Instance;
	}

	public async Task SignTransactionAsync(TransactionSignaturesRequest request, CancellationToken cancellationToken)
	{
		var start = DateTimeOffset.UtcNow;

		var roundState = RoundsRegistry.GetRoundState(request.RoundId, Phase.TransactionSigning);
		using var releaser = await RoundsRegistry.LockRoundAsync(roundState.Id).ConfigureAwait(false);
		var round = releaser.Round;

		var state = round.Assert<SigningState>().AddWitness((int)request.InputIndex, request.Witness);

		// at this point all of the witnesses have been verified and the state can be updated
		round.CoinjoinState = state;

		ArenaTimeBenchmarker.AddSignTransaction(DateTimeOffset.UtcNow - start);
	}

	public async Task<ReissueCredentialResponse> ReissuanceAsync(ReissueCredentialRequest request, CancellationToken cancellationToken)
	{
		var start = DateTimeOffset.UtcNow;
		Round round;

		var roundState = RoundsRegistry.GetRoundState(request.RoundId, Phase.ConnectionConfirmation, Phase.OutputRegistration);
		using (var releaser = await RoundsRegistry.LockRoundAsync(roundState.Id).ConfigureAwait(false))
		{
			round = releaser.Round;
		}

		ArenaTimeBenchmarker.AddReissuanceLock(DateTimeOffset.UtcNow - start);

		if (request.RealAmountCredentialRequests.Delta != 0)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.DeltaNotZero, $"Round ({round.Id}): Amount credentials delta must be zero.");
		}

		if (request.RealVsizeCredentialRequests.Delta != 0)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.DeltaNotZero, $"Round ({round.Id}): Vsize credentials delta must be zero.");
		}

		if (request.RealAmountCredentialRequests.Requested.Count() != ProtocolConstants.CredentialNumber)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongNumberOfCreds, $"Round ({round.Id}): Incorrect requested number of amount credentials.");
		}

		if (request.RealVsizeCredentialRequests.Requested.Count() != ProtocolConstants.CredentialNumber)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongNumberOfCreds, $"Round ({round.Id}): Incorrect requested number of weight credentials.");
		}

		var realAmountTask = round.AmountCredentialIssuer.HandleRequestAsync(request.RealAmountCredentialRequests, cancellationToken);
		var realVsizeTask = round.VsizeCredentialIssuer.HandleRequestAsync(request.RealVsizeCredentialRequests, cancellationToken);
		var zeroAmountTask = round.AmountCredentialIssuer.HandleRequestAsync(request.ZeroAmountCredentialRequests, cancellationToken);
		var zeroVsizeTask = round.VsizeCredentialIssuer.HandleRequestAsync(request.ZeroVsizeCredentialsRequests, cancellationToken);

		var result = new ReissueCredentialResponse(
			await realAmountTask.ConfigureAwait(false),
			await realVsizeTask.ConfigureAwait(false),
			await zeroAmountTask.ConfigureAwait(false),
			await zeroVsizeTask.ConfigureAwait(false));

		ArenaTimeBenchmarker.AddReissuance(DateTimeOffset.UtcNow - start);
		return result;
	}

	public async Task<Coin> OutpointToCoinAsync(InputRegistrationRequest request, CancellationToken cancellationToken)
	{
		OutPoint input = request.Input;

		if (Prison.TryGet(input, out var inmate) && (!Config.AllowNotedInputRegistration || inmate.Punishment != Punishment.Noted))
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.InputBanned);
		}

		var txOutResponse = await Rpc.GetTxOutAsync(input.Hash, (int)input.N, includeMempool: true, cancellationToken).ConfigureAwait(false);
		if (txOutResponse is null)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.InputSpent);
		}
		if (txOutResponse.Confirmations == 0)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.InputUnconfirmed);
		}
		if (txOutResponse.IsCoinBase && txOutResponse.Confirmations <= 100)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.InputImmature);
		}

		return new Coin(input, txOutResponse.TxOut);
	}

	public Task<RoundStateResponse> GetStatusAsync(RoundStateRequest request, CancellationToken cancellationToken)
	{
		var start = DateTimeOffset.UtcNow;

		var roundStates = RoundsRegistry.RoundStates.Select(x =>
		{
			var checkPoint = request.RoundCheckpoints.FirstOrDefault(y => y.RoundId == x.Id);
			return x.FromStateId(checkPoint == default ? 0 : checkPoint.StateId);
		}).ToArray();

		ArenaTimeBenchmarker.AddGetStatus(DateTimeOffset.UtcNow - start);
		return Task.FromResult(new RoundStateResponse(roundStates, Array.Empty<CoinJoinFeeRateMedian>()));
	}

	private Alice GetAlice(Guid aliceId, Round round) =>
		round.Alices.Find(x => x.Id == aliceId)
		?? throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AliceNotFound, $"Round ({round.Id}): Alice ({aliceId}) not found.");

	private static bool IsUserCheating(Exception e) =>
		e is WabiSabiCryptoException || (e is WabiSabiProtocolException wpe && wpe.ErrorCode.IsEvidencingClearMisbehavior());
}
