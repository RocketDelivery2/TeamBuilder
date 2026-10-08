using FluentAssertions;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

public class DiscoveryCursorTests
{
    private static readonly DateTime From = new(2026, 10, 14, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RoundTrips_TheExactDistanceBits_StartIdAndWindow()
    {
        var distance = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(16093.44) + 1); // next representable double
        var cursor = new DiscoveryCursor(distance, From.AddHours(20), Guid.NewGuid(), From, From.AddDays(7), "abcdef0123456789");

        var parsed = DiscoveryCursor.TryParse(cursor.Encode());

        parsed.Should().Be(cursor);
        BitConverter.DoubleToInt64Bits(parsed!.Value.DistanceMeters).Should().Be(BitConverter.DoubleToInt64Bits(distance));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("ZDE6eHg6eHg6eHg6eHg6eHg6eHg")]
    public void TryParse_RejectsForeignText(string text) =>
        DiscoveryCursor.TryParse(text).Should().BeNull();

    [Fact]
    public void Hash_ChangesWithEverySearchParameter_AndHoldsNoCoordinates()
    {
        var query = new DiscoverOccurrencesQuery(41.878113, -87.629799, 15, "basketball", null, null, false, 20, null);
        var hash = DiscoveryCursor.HashOf(query, From, From.AddDays(7));

        hash.Should().MatchRegex("^[0-9a-f]{16}$");
        foreach (var other in new[]
        {
            query with { Latitude = 41.878114 },
            query with { Longitude = -87.629798 },
            query with { RadiusMiles = 25 },
            query with { Activity = null },
            query with { OpenOnly = true }
        })
            DiscoveryCursor.HashOf(other, From, From.AddDays(7)).Should().NotBe(hash);
        DiscoveryCursor.HashOf(query, From.AddSeconds(1), From.AddDays(7)).Should().NotBe(hash);
        DiscoveryCursor.HashOf(query with { PageSize = 50, Cursor = "x" }, From, From.AddDays(7)).Should().Be(hash, "page size and cursor do not change the result set");
    }
}
