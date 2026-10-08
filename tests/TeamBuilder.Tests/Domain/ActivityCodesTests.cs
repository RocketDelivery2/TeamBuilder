using FluentAssertions;
using TeamBuilder.Domain;

namespace TeamBuilder.Tests.Domain;

public class ActivityCodesTests
{
    [Theory]
    [InlineData("basketball", "basketball")]
    [InlineData("  Basketball ", "basketball")]
    [InlineData("BASKETBALL", "basketball")]
    [InlineData("ultimate-frisbee", "ultimate-frisbee")]
    [InlineData("wow.raid_25", "wow.raid_25")]
    public void TryNormalize_TrimsAndLowercases(string input, string expected)
    {
        ActivityCodes.TryNormalize(input, out var normalized, out _).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%basketball%")]
    [InlineData("basket ball")]
    [InlineData("-basketball")]
    [InlineData("basketball'; DROP TABLE Events;--")]
    public void TryNormalize_RejectsAnythingButACode(string input)
    {
        ActivityCodes.TryNormalize(input, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryNormalize_RejectsOverlongCodes() =>
        ActivityCodes.TryNormalize(new string('a', ActivityCodes.MaxLength + 1), out _, out _).Should().BeFalse();
}
