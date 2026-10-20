using System.ComponentModel.DataAnnotations;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Application.DTOs;

/// <summary>Body of <c>POST /api/v1/players/me/api-keys</c>.</summary>
public class CreateIntegrationApiKeyDto : IValidatableObject
{
    public const int NameMaxLength = 80;
    public const int MaxExpiresInDays = 365;

    /// <summary>A label for the key, e.g. "League website". Required.</summary>
    [Required]
    [StringLength(NameMaxLength, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>Days until the key expires (1 to 365). Omit for a key that never expires.</summary>
    [Range(1, MaxExpiresInDays)]
    public int? ExpiresInDays { get; set; }

    /// <summary>Scopes to grant. Omit for the default (<c>rosters:read</c>).</summary>
    public IReadOnlyList<string>? Scopes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Name is not null && string.IsNullOrWhiteSpace(Name))
            yield return new ValidationResult("Name must not be blank.", [nameof(Name)]);
        if (Scopes is { Count: 0 })
            yield return new ValidationResult("Scopes must not be empty; omit it for the default.", [nameof(Scopes)]);
        foreach (var scope in Scopes ?? [])
        {
            if (!IntegrationScopes.IsKnown(scope))
                yield return new ValidationResult($"Unknown scope. Known scopes: {string.Join(", ", IntegrationScopes.All)}.", [nameof(Scopes)]);
        }
    }
}

/// <summary>An integration API key as its owner sees it. Never carries the key or its hash.</summary>
public class IntegrationApiKeyDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The non-secret start of the key (<c>tbk_{prefix}</c>), to tell keys apart.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    public IReadOnlyList<string> Scopes { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Not revoked and not expired.</summary>
    public bool IsActive { get; set; }
}

/// <summary>The creation response: the key itself, shown exactly once.</summary>
public class CreatedIntegrationApiKeyDto : IntegrationApiKeyDto
{
    /// <summary>The full key for the <c>X-Api-Key</c> header. It cannot be retrieved again.</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>The authenticated key, as resolved from a presented <c>X-Api-Key</c>.</summary>
public sealed record IntegrationApiKeyIdentity(Guid KeyId, Guid PlayerId, IReadOnlyList<string> Scopes);

/// <summary><c>GET /api/integration/v1/me</c>: which key is calling and for whom.</summary>
public class IntegrationCallerDto
{
    public Guid KeyId { get; set; }
    public string KeyName { get; set; } = string.Empty;
    public IReadOnlyList<string> Scopes { get; set; } = [];
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>The TeamBuilder player the key acts for.</summary>
    public Guid PlayerId { get; set; }
    public string? DisplayName { get; set; }
}
