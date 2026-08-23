using System.Threading.Channels;

namespace Orf;

/// <summary>
/// Fans a single live MP3 byte stream (game audio via openal-soft's wave backend →
/// FIFO → ffmpeg) out to any number of dashboard listeners. Slow clients drop old
/// chunks rather than stalling the pump; MP3 frames self-sync so they just blip.
/// </summary>
public sealed class AudioBroadcaster
{
	/// <summary>Set by MatchRunner when game audio is live; null = no audio this run.</summary>
	public static AudioBroadcaster? Instance;

	readonly Lock sync = new();
	readonly List<Channel<byte[]>> clients = [];

	/// <summary>Reads the ffmpeg stdout stream to EOF, broadcasting each chunk.</summary>
	public async Task PumpAsync(Stream source, CancellationToken ct)
	{
		var buf = new byte[8192];
		try
		{
			while (!ct.IsCancellationRequested)
			{
				var n = await source.ReadAsync(buf, ct);
				if (n <= 0)
					break;

				var chunk = buf[..n];
				lock (sync)
				{
					foreach (var c in clients)
						c.Writer.TryWrite(chunk);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (IOException)
		{
		}
	}

	public Channel<byte[]> Subscribe()
	{
		var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
		});

		lock (sync)
			clients.Add(channel);
		return channel;
	}

	public void Unsubscribe(Channel<byte[]> channel)
	{
		lock (sync)
			clients.Remove(channel);
	}
}
