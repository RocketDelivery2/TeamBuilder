using System.ComponentModel.DataAnnotations;

namespace TeamBuilder.Application.Validation;

/// <summary>
/// Validates that a <see cref="Guid"/> or <see cref="Guid?"/> value is not <see cref="Guid.Empty"/>.
/// Use together with <see cref="RequiredAttribute"/> on nullable Guid? properties so that both
/// missing JSON (null) and all-zero GUIDs are rejected.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class NonEmptyGuidAttribute : ValidationAttribute
{
    public NonEmptyGuidAttribute() : base("The {0} field must not be an empty GUID.")
    {
    }

    public override bool IsValid(object? value)
    {
        return value switch
        {
            null => true, // null handled by [Required]; let it pass here
            Guid g => g != Guid.Empty,
            _ => true
        };
    }
}
