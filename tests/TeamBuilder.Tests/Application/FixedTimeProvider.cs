namespace TeamBuilder.Tests.Application;

/// <summary>A settable clock for code that takes <see cref="TimeProvider"/>.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
