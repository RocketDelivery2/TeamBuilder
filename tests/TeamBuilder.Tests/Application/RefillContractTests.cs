using System.Text.Json;
using FluentAssertions;
using TeamBuilder.Application.Outbox;
using TeamBuilder.Domain.Outbox;
using TeamBuilder.Infrastructure.Outbox;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>The durable vacancy contract, notification wording and outbox retry schedule.</summary>
public class RefillContractTests
{
    private static RosterVacancyOpenedV1 Sample() => new()
    {
        EventId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        OccurrenceId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        RosterRequirementId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        RoleCode = "participant",
        VacatedAssignmentId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        Reason = VacancyReason.NoShow,
        PreviousOpenQuantity = 0,
        OpenQuantity = 1,
        RequiredCount = 10,
        OccurrenceStartUtc = new DateTime(2026, 10, 15, 1, 0, 0, DateTimeKind.Utc),
        VenueId = null,
        OccurredAtUtc = new DateTime(2026, 10, 15, 0, 30, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void Payload_IsCamelCaseJson_WithTheReasonByName_AndRoundTrips()
    {
        var json = Sample().ToJson();

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("reason").GetString().Should().Be("NoShow");
        document.RootElement.GetProperty("eventId").GetGuid().Should().Be(Sample().EventId);
        document.RootElement.GetProperty("occurrenceStartUtc").GetString().Should().Be("2026-10-15T01:00:00Z");
        json.Should().NotContain("TeamBuilder.", "no CLR type names in the durable contract");
        RosterVacancyOpenedV1.FromJson(json).Should().Be(Sample());
    }

    [Fact]
    public void Payload_HasExactlyTheAgreedFields_AndNoPersonalData()
    {
        typeof(RosterVacancyOpenedV1).GetProperties().Where(p => p.DeclaringType == typeof(RosterVacancyOpenedV1) && !p.GetMethod!.IsStatic)
            .Select(p => p.Name).Should().BeEquivalentTo(
                "EventId", "OccurrenceId", "RosterRequirementId", "RoleCode", "VacatedAssignmentId", "Reason",
                "PreviousOpenQuantity", "OpenQuantity", "RequiredCount", "OccurrenceStartUtc", "VenueId", "OccurredAtUtc");
    }

    [Fact]
    public void EventTypeAndDeduplicationKey_AreStableStrings()
    {
        RosterVacancyOpenedV1.EventType.Should().Be("roster.vacancy.opened.v1");
        RosterVacancyOpenedV1.DeduplicationKeyFor(Guid.Parse("44444444-4444-4444-4444-444444444444"))
            .Should().Be("roster.vacancy.opened.v1:44444444444444444444444444444444");
    }

    [Theory]
    [InlineData("basketball", "Basketball spot opened")]
    [InlineData("  soccer ", "Soccer spot opened")]
    [InlineData(null, "Spot opened")]
    [InlineData("", "Spot opened")]
    public void Title_NamesTheActivity_NeverAPerson(string? category, string expected)
    {
        RosterVacancyNotificationHandler.TitleFor(category).Should().Be(expected);
    }

    [Theory]
    [InlineData("participant", null, "Wednesday Basketball", "A participant spot opened in Wednesday Basketball.")]
    [InlineData("point_guard", "Point Guard", "Rec league", "A Point Guard spot opened in Rec league.")]
    [InlineData("healer", null, "Raid night", "A healer spot opened in Raid night.")]
    [InlineData("attacker", null, "Futsal", "An attacker spot opened in Futsal.")]
    public void Body_NamesTheRoleAndGame_NeverWhoLeft(string roleCode, string? displayPosition, string name, string expected)
    {
        RosterVacancyNotificationHandler.BodyFor(roleCode, displayPosition, name).Should().Be(expected);
    }

    [Fact]
    public void RetryDelay_DoublesFromTheBase_UpToTheCap()
    {
        var options = new OutboxOptions { RetryBaseDelay = TimeSpan.FromSeconds(5), RetryMaxDelay = TimeSpan.FromMinutes(1) };

        Enumerable.Range(1, 6).Select(options.RetryDelayFor).Should().Equal(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void NotificationCursor_RoundTrips_AndRejectsForeignCursors()
    {
        var cursor = new NotificationCursor(new DateTime(2026, 10, 9, 2, 0, 0, DateTimeKind.Utc), Guid.NewGuid());

        NotificationCursor.Parse(cursor.Encode()).Should().Be(cursor);
        foreach (var bad in new[] { "", "not-a-cursor", Convert.ToBase64String("d1:1:2"u8.ToArray()) })
            ((Action)(() => NotificationCursor.Parse(bad))).Should().Throw<ArgumentException>(bad);
    }
}
