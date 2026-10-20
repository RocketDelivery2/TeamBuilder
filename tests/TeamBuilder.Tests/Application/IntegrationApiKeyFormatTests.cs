using FluentAssertions;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>Integration API key format (tbk_{prefix}_{secret}) and the contract's code values.</summary>
public class IntegrationApiKeyFormatTests
{
    [Fact]
    public void Generate_ProducesDistinctWellFormedKeys_WhosePrefixParsesBack()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => IntegrationApiKeyFormat.Generate()).ToList();

        keys.Select(k => k.Key).Should().OnlyHaveUniqueItems();
        foreach (var (key, prefix) in keys)
        {
            key.Should().HaveLength(IntegrationApiKeyFormat.KeyLength).And.StartWith("tbk_" + prefix + "_");
            IntegrationApiKeyFormat.TryParse(key, out var parsed).Should().BeTrue(key);
            parsed.Should().Be(prefix);
            IntegrationApiKeyFormat.Matches(key, IntegrationApiKeyFormat.Hash(key)).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tbk_")]
    [InlineData("tbk_0123456789AB_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // uppercase prefix
    [InlineData("tbx_0123456789ab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // marker
    [InlineData("tbk_0123456789ab-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // separator
    [InlineData("tbk_0123456789ab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]  // short secret
    [InlineData("tbk_0123456789ab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=")] // padding
    [InlineData("tbk_0123456789ab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ")] // whitespace
    public void TryParse_RejectsAnythingButTheExactFormat(string? key)
    {
        IntegrationApiKeyFormat.TryParse(key, out var prefix).Should().BeFalse();
        prefix.Should().BeEmpty();
    }

    [Fact]
    public void Matches_IsFalseForAnotherKeyOrAMalformedHash()
    {
        var (key, _) = IntegrationApiKeyFormat.Generate();
        var (other, _) = IntegrationApiKeyFormat.Generate();

        IntegrationApiKeyFormat.Matches(other, IntegrationApiKeyFormat.Hash(key)).Should().BeFalse();
        IntegrationApiKeyFormat.Matches(key, []).Should().BeFalse();
        IntegrationApiKeyFormat.Matches(key, IntegrationApiKeyFormat.Hash(key)[..16]).Should().BeFalse();
    }

    [Fact]
    public void ContractCodes_AreLowercaseSnakeCaseNames()
    {
        IntegrationRosterService.ContractCode(EventStatus.InProgress).Should().Be("in_progress");
        IntegrationRosterService.ContractCode(RosterAssignmentStatus.CheckedIn).Should().Be("checked_in");
        IntegrationRosterService.ContractCode(RosterAssignmentStatus.NoShow).Should().Be("no_show");
        IntegrationRosterService.ContractCode(VenuePrivacyLevel.Private).Should().Be("private");
        IntegrationRosterService.ContractCode(RosterAssignmentSource.Player).Should().Be("player");
    }
}
