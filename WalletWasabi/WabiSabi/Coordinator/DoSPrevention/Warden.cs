using Microsoft.Extensions.Hosting;
using System.IO;
using System.Threading.Channels;
using WalletWasabi.Helpers;

namespace WalletWasabi.WabiSabi.Coordinator.DoSPrevention;

public class Warden : BackgroundService
{
	public Warden(string prisonFilePath)
	{
		_prisonFilePath = prisonFilePath;
		_offendersToSaveChannel = Channel.CreateUnbounded<Offender>();

		Prison = DeserializePrison(_prisonFilePath, _offendersToSaveChannel.Writer);
	}

	public Prison Prison { get; }

	private readonly string _prisonFilePath;

	private readonly Channel<Offender> _offendersToSaveChannel;
	private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private static Prison DeserializePrison(string prisonFilePath, ChannelWriter<Offender> channelWriter)
	{
		IoHelpers.EnsureContainingDirectoryExists(prisonFilePath);
		var offenders = new List<Offender>();
		if (File.Exists(prisonFilePath))
		{
			try
			{
				foreach (var offender in File.ReadAllLines(prisonFilePath).Select(Offender.FromStringLine))
				{
					offenders.Add(offender);
				}
			}
			catch (Exception ex)
			{
				Logger.LogError(ex);
				Logger.LogWarning($"Deleting {prisonFilePath}");
				File.Delete(prisonFilePath);
			}
		}

		return new Prison(offenders, channelWriter);
	}

	protected override async Task ExecuteAsync(CancellationToken cancellationToken)
	{
		_started.TrySetResult();
		using var registration = cancellationToken.Register(() => _offendersToSaveChannel.Writer.TryComplete());

		try
		{
			await foreach (var inmate in _offendersToSaveChannel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
			{
				await File.AppendAllLinesAsync(_prisonFilePath, [inmate.ToStringLine()], CancellationToken.None).ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			throw;
		}
	}

	public override async Task StartAsync(CancellationToken cancellationToken)
	{
		await base.StartAsync(cancellationToken).ConfigureAwait(false);
		await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	public override Task StopAsync(CancellationToken cancellationToken)
	{
		_offendersToSaveChannel.Writer.TryComplete();
		return base.StopAsync(cancellationToken);
	}
}
