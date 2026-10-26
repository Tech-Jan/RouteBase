using System.Diagnostics;
using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.ErrorLog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RouteBaseWeb.Tests.Services.ErrorLog;

/// <summary>
/// Drain semantics for <see cref="ErrorLogWriteService"/>: entries reach the store, a throwing store is
/// swallowed and the loop keeps draining, and the suppression flag is set during the store call (asserted via
/// an instrumented fake store). The store is faked through a hand-rolled scope factory returning it for
/// GetRequiredService&lt;ErrorLogStore&gt;.
/// </summary>
public class ErrorLogWriteServiceTests
{
    private sealed class ProbeStore : ErrorLogStore
    {
        public readonly List<ErrorLogEntry> Written = new();
        public bool? SuppressionDuringWrite;
        public ProbeStore() : base(null!, TimeProvider.System) { }
        public override Task WriteAsync(ErrorLogEntry e, CancellationToken ct = default)
        {
            SuppressionDuringWrite = ErrorLogSuppression.Active;
            Written.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStore : ErrorLogStore
    {
        public int Calls;
        public ThrowingStore() : base(null!, TimeProvider.System) { }
        public override Task WriteAsync(ErrorLogEntry e, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("store down");
        }
    }

    private sealed class FakeScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly ErrorLogStore _store;
        public FakeScopeFactory(ErrorLogStore store) => _store = store;
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ErrorLogStore) ? _store : null;
    }

    private static ErrorLogEntry Entry(string message) =>
        new(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            "background", "Cat", "Error", message, null, null, null, null);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
    }

    [Fact]
    public async Task EnqueuedEntries_ReachStore_WithSuppressionActiveDuringWrite()
    {
        var channel = new ErrorLogChannel();
        var store = new ProbeStore();
        var svc = new ErrorLogWriteService(channel, new FakeScopeFactory(store));

        await svc.StartAsync(CancellationToken.None);
        channel.TryWrite(Entry("a"));
        channel.TryWrite(Entry("b"));
        await WaitUntilAsync(() => store.Written.Count == 2);
        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "a", "b" }, store.Written.Select(e => e.Message), StringComparer.Ordinal);
        Assert.True(store.SuppressionDuringWrite);          // flag on during the store call
        Assert.False(ErrorLogSuppression.Active);           // and reset after
    }

    [Fact]
    public async Task ThrowingStore_Swallowed_LoopKeepsDraining()
    {
        var channel = new ErrorLogChannel();
        var store = new ThrowingStore();
        var svc = new ErrorLogWriteService(channel, new FakeScopeFactory(store));

        await svc.StartAsync(CancellationToken.None);
        channel.TryWrite(Entry("a"));
        channel.TryWrite(Entry("b"));
        channel.TryWrite(Entry("c"));
        await WaitUntilAsync(() => store.Calls == 3);
        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(3, store.Calls); // every entry attempted despite each throwing
    }
}
