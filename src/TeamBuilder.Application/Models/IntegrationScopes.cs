namespace TeamBuilder.Application.Models;

/// <summary>
/// Scopes an integration API key can hold. Every key is limited to what its owner hosts; a
/// scope narrows that further to a kind of access. v1 has one read scope; write scopes will be
/// added as separate values so an existing key never gains access it was not created with.
/// </summary>
public static class IntegrationScopes
{
    /// <summary>Read the owner's hosted games and their rosters.</summary>
    public const string RostersRead = "rosters:read";

    public static readonly IReadOnlyList<string> All = [RostersRead];

    /// <summary>The scopes a new key gets when the request names none.</summary>
    public static readonly IReadOnlyList<string> Default = [RostersRead];

    public static bool IsKnown(string scope) => All.Contains(scope, StringComparer.Ordinal);

    public static string Join(IEnumerable<string> scopes) => string.Join(' ', scopes);

    public static IReadOnlyList<string> Split(string? scopes) =>
        string.IsNullOrWhiteSpace(scopes) ? [] : scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
