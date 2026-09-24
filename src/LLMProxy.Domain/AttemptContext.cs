using System.Collections.Concurrent;

namespace LLMProxy.Domain;

public sealed class AttemptContext(Guid requestId, ModelTarget target, TimeProvider time)
{
    private static readonly AsyncLocal<AttemptContext?> Ambient = new();
    public static AttemptContext? Current => Ambient.Value;
    public ConcurrentQueue<UpstreamAttempt> Attempts { get; } = new();
    public TimeProvider Time { get; } = time;
    public UpstreamAttempt Start()
    {
        var attempt = new UpstreamAttempt
        {
            RequestId = requestId,
            Provider = target.Provider,
            Model = target.Model,
            CreatedAt = Time.GetUtcNow()
        };
        Attempts.Enqueue(attempt);
        return attempt;
    }
    public IDisposable Enter()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new Scope(() => Ambient.Value = previous);
    }
    private sealed class Scope(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
