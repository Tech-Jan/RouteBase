using System.Threading.Channels;

namespace RouteBaseWeb.Services.ErrorLog;

/// <summary>
/// Singleton wrapper over the bounded channel carrying captured entries from the synchronous ILogger.Log
/// path (<see cref="TryWrite"/>, non-blocking) to the drain BackgroundService (<see cref="Reader"/>). Bounded
/// at 1024 with DropWrite: under an error storm the earliest entries — which carry the root cause — are
/// kept and later ones dropped, so the log call never blocks and memory stays capped.
/// </summary>
public sealed class ErrorLogChannel
{
    private readonly Channel<ErrorLogEntry> _channel =
        Channel.CreateBounded<ErrorLogEntry>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <summary>Non-blocking enqueue; returns false (drops) when the channel is full.</summary>
    public bool TryWrite(ErrorLogEntry entry) => _channel.Writer.TryWrite(entry);

    public ChannelReader<ErrorLogEntry> Reader => _channel.Reader;
}
