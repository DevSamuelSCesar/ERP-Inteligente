namespace Nexus.Tests.Core;

/// <summary>A <see cref="TimeProvider"/> whose clock moves only when a test sets or advances it.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow = start;

    /// <summary>The current instant returned by <see cref="GetUtcNow"/>.</summary>
    public DateTimeOffset UtcNow
    {
        get { lock (_gate) return _utcNow; }
        set { lock (_gate) _utcNow = value; }
    }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta)
    {
        lock (_gate) _utcNow += delta;
    }
}
