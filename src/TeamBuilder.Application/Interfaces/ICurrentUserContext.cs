namespace TeamBuilder.Application.Interfaces;

/// <summary>
/// Provides the identity of the caller for the current request.
/// The concrete implementation reads from the HTTP context; a test
/// double or future claims-based implementation can be substituted
/// without changing any controller or service code.
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>
    /// The player ID of the current caller.
    /// Returns <see cref="Guid.Empty"/> when no identity is present.
    /// </summary>
    Guid UserId { get; }
}
