namespace TeamBuilder.Application.Exceptions;

/// <summary>
/// A discovery request parameter that only the service can judge (the search window once its
/// defaults are applied, or the cursor). Mapped to a 400 validation problem on
/// <see cref="Field"/>. The message never contains the caller's search point.
/// </summary>
public sealed class DiscoveryValidationException : ArgumentException
{
    public DiscoveryValidationException(string field, string message)
        : base(message)
    {
        Field = field;
    }

    public string Field { get; }
}
