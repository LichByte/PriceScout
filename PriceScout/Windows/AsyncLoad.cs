using System;
using System.Threading;
using System.Threading.Tasks;

namespace PriceScout.Windows;

// Runs one background request at a time and exposes its state for drawing.
// Key identifies what was requested, so callers can tell when a reload is needed.
public sealed class AsyncLoad<T> : IDisposable where T : class
{
    private CancellationTokenSource? cts;

    public string? Key { get; private set; }
    public T? Value { get; private set; }
    public string? Error { get; private set; }
    public bool Loading { get; private set; }

    public void Start(string key, Func<CancellationToken, Task<T>> load)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;

        Key = key;
        Value = null;
        Error = null;
        Loading = true;

        Task.Run(async () =>
        {
            try
            {
                var value = await load(token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                    Value = value;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Request failed ({key})");
                if (!token.IsCancellationRequested)
                    Error = UniversalisClient.Describe(ex);
            }
            finally
            {
                if (!token.IsCancellationRequested)
                    Loading = false;
            }
        }, token);
    }

    // Starts a load only if the key changed since the last one.
    public void Ensure(string key, Func<CancellationToken, Task<T>> load)
    {
        if (Key != key)
            Start(key, load);
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}
