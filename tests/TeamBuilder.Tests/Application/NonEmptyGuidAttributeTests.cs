using FluentAssertions;
using TeamBuilder.Application.Validation;

namespace TeamBuilder.Tests.Application;

public class NonEmptyGuidAttributeTests
{
    private readonly NonEmptyGuidAttribute _attribute = new();

    [Fact]
    public void IsValid_ShouldReturnFalse_WhenGuidIsEmpty()
    {
        _attribute.IsValid(Guid.Empty).Should().BeFalse();
    }

    [Fact]
    public void IsValid_ShouldReturnTrue_WhenGuidIsNonEmpty()
    {
        _attribute.IsValid(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void IsValid_ShouldReturnTrue_WhenValueIsNull()
    {
        // null defers to [Required]; NonEmptyGuid must not duplicate that error
        _attribute.IsValid(null).Should().BeTrue();
    }

    [Fact]
    public void IsValid_ShouldReturnFalse_WhenValueIsUnsupportedNonNullType()
    {
        // Accidental misuse on a string property must fail closed
        _attribute.IsValid("not-a-guid").Should().BeFalse();
    }

    [Fact]
    public void IsValid_ShouldReturnFalse_WhenValueIsUnsupportedNumericType()
    {
        _attribute.IsValid(42).Should().BeFalse();
    }
}
