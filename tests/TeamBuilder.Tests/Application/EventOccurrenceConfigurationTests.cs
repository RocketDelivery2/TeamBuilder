using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// EF Core model metadata for the event-occurrence foundation (SQL Server provider, no
/// connection needed). The real-database behavior is proven separately in
/// EventOccurrenceSchemaSqlServerIntegrationTests and the migration integration tests.
/// </summary>
public class EventOccurrenceConfigurationTests
{
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer("Server=unused;Database=unused")
            .Options;
        using var context = new TeamBuilderDbContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void EventOccurrence_MapsToTheExistingEventsTable()
    {
        var model = Model();
        model.FindEntityType(typeof(EventOccurrence))!.GetTableName().Should().Be("Events");
        model.GetEntityTypes().Select(e => e.GetTableName())
            .Should().NotContain(t => t!.Contains("Occurrence"));
    }

    [Fact]
    public void RosterEntry_StillReferencesEventsByEventId_WithCascade()
    {
        var fk = Model().FindEntityType(typeof(RosterEntry))!.GetForeignKeys()
            .Single(f => f.PrincipalEntityType.ClrType == typeof(EventOccurrence));

        fk.Properties.Select(p => p.Name).Should().Equal("EventId");
        fk.IsRequired.Should().BeTrue();
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        fk.PrincipalEntityType.GetTableName().Should().Be("Events");
    }

    [Theory]
    [InlineData(typeof(EventOccurrence), nameof(EventOccurrence.HostId), DeleteBehavior.SetNull)]
    [InlineData(typeof(EventOccurrence), nameof(EventOccurrence.TeamId), DeleteBehavior.SetNull)]
    [InlineData(typeof(EventOccurrence), nameof(EventOccurrence.SeriesId), DeleteBehavior.SetNull)]
    [InlineData(typeof(EventOccurrence), nameof(EventOccurrence.VenueId), DeleteBehavior.Restrict)]
    [InlineData(typeof(EventSeries), nameof(EventSeries.HostId), DeleteBehavior.SetNull)]
    [InlineData(typeof(EventSeries), nameof(EventSeries.TeamId), DeleteBehavior.SetNull)]
    [InlineData(typeof(EventSeries), nameof(EventSeries.VenueId), DeleteBehavior.Restrict)]
    public void ForeignKeys_UseTheIntendedDeleteBehavior(Type entity, string property, DeleteBehavior expected)
    {
        var fk = Model().FindEntityType(entity)!.GetForeignKeys()
            .Single(f => f.Properties.Single().Name == property);

        fk.DeleteBehavior.Should().Be(expected);
        fk.IsRequired.Should().BeFalse();
    }

    [Theory]
    [InlineData(nameof(EventOccurrence.SeriesId))]
    [InlineData(nameof(EventOccurrence.TeamId))]
    [InlineData(nameof(EventOccurrence.VenueId))]
    [InlineData(nameof(EventOccurrence.HostId))]
    [InlineData(nameof(EventOccurrence.ScheduledEndUtc))]
    [InlineData(nameof(EventOccurrence.OccurrenceIndex))]
    [InlineData(nameof(EventOccurrence.LegacyLocation))]
    public void EventOccurrence_OptionalColumnsAreNullable(string property)
    {
        Model().FindEntityType(typeof(EventOccurrence))!.FindProperty(property)!.IsNullable.Should().BeTrue();
    }

    [Fact]
    public void EventOccurrence_DisplayLocationIsNotMapped()
    {
        Model().FindEntityType(typeof(EventOccurrence))!.FindProperty(nameof(EventOccurrence.DisplayLocation)).Should().BeNull();
    }

    [Fact]
    public void EventOccurrence_HasOnlyTheFilteredSeriesStartUniqueIndex()
    {
        var unique = Model().FindEntityType(typeof(EventOccurrence))!.GetIndexes()
            .Where(i => i.IsUnique)
            .Should().ContainSingle().Which;
        unique.GetDatabaseName().Should().Be("UX_Events_SeriesId_ScheduledStartUtc");
        unique.Properties.Select(p => p.Name).Should().Equal(nameof(EventOccurrence.SeriesId), nameof(EventOccurrence.ScheduledStartUtc));
        unique.GetFilter().Should().Be("[SeriesId] IS NOT NULL");
    }

    [Theory]
    [InlineData(nameof(Venue.Latitude))]
    [InlineData(nameof(Venue.Longitude))]
    public void Venue_CoordinatesAreNullableDecimal9_6(string property)
    {
        var column = Model().FindEntityType(typeof(Venue))!.FindProperty(property)!;
        column.IsNullable.Should().BeTrue();
        column.GetPrecision().Should().Be(9);
        column.GetScale().Should().Be(6);
    }

    [Fact]
    public void Venue_DeclaresCoordinateChecks_AndSeriesDeclaresDurationAndParticipantChecks()
    {
        var model = Model();
        model.FindEntityType(typeof(Venue))!.GetCheckConstraints().Select(c => c.Name)
            .Should().BeEquivalentTo("CK_Venues_Latitude_Range", "CK_Venues_Longitude_Range");
        model.FindEntityType(typeof(EventSeries))!.GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql)
            .Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["CK_EventSeries_DurationMinutes_Range"] = "[DurationMinutes] > 0 AND [DurationMinutes] <= 10080",
                ["CK_EventSeries_MaxParticipants_Range"] = "[MaxParticipants] >= 1 AND [MaxParticipants] <= 100000"
            });
    }

    [Fact]
    public void OccurrenceDisplayLocation_PrefersVenueName_ThenLegacyText()
    {
        new EventOccurrence { LegacyLocation = "text" }.DisplayLocation.Should().Be("text");
        new EventOccurrence { LegacyLocation = "text", Venue = new Venue { Name = "Gym" } }.DisplayLocation.Should().Be("Gym");
        new EventOccurrence().DisplayLocation.Should().BeNull();
    }
}
