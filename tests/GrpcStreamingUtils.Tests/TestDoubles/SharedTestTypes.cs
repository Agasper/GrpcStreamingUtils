using Grpc.Core;

namespace GrpcStreamingUtils.Tests.TestDoubles;

internal class TestIncoming
{
    public string Data { get; set; } = "";
}

internal class TestOutgoing
{
    public string Data { get; set; } = "";
}

internal class InMemoryStreamReader<T> : IAsyncStreamReader<T> where T : class
{
    private readonly IEnumerator<T> _enumerator;

    public InMemoryStreamReader(IEnumerable<T> items)
    {
        _enumerator = items.GetEnumerator();
    }

    public T Current => _enumerator.Current;

    public Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_enumerator.MoveNext());
    }
}

internal class BlockingStreamReader<T> : IAsyncStreamReader<T> where T : class
{
    private readonly CancellationToken _ct;

    public BlockingStreamReader(CancellationToken ct)
    {
        _ct = ct;
    }

    public T Current => throw new InvalidOperationException();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ct);
        try
        {
            await Task.Delay(Timeout.Infinite, linked.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        return false;
    }
}

internal class ThrowingStreamReader<T> : IAsyncStreamReader<T> where T : class
{
    private readonly Exception _exception;

    public ThrowingStreamReader(Exception exception)
    {
        _exception = exception;
    }

    public T Current => throw new InvalidOperationException();

    public Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        throw _exception;
    }
}
