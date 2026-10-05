using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Runtime behavior of the event-occurrence foundation on a real, fully migrated SQL Server
/// database: nullable series/team/venue/coordinates, the coordinate and duration CHECKs, and
/// that deleting a host, team, series or venue never deletes occurrence history. Deletes are
/// issued as raw SQL so the database's own referential actions are what is being tested, not
/// EF Core's client-side fix-up.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class EventOccurrenceSchemaSqlServerIntegrationTests : IAsyncLifetime
{
    private const int CheckConstraintViolation = 547;

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public EventOccurrenceSchemaSqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "occurrenceschema");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task OneOffPickupOccurrence_AllowsNullSeriesTeamVenueAndEnd()
    {
        var id = await AddOccurrenceAsync(o => o.LegacyLocation = "Pickup court");

        await using var context = _db.CreateContext();
        var stored = await context.Events.AsNoTracking().SingleAsync(e => e.Id == id);
        stored.SeriesId.Should().BeNull();
        stored.TeamId.Should().BeNull();
        stored.VenueId.Should().BeNull();
        stored.ScheduledEndUtc.Should().BeNull();
        stored.IsDetached.Should().BeFalse();
        stored.LegacyLocation.Should().Be("Pickup court");
    }

    [Fact]
    public async Task Venue_AllowsNullCoordinatesAndTimeZone()
    {
        var id = await AddVenueAsync(v => v.VenueType = VenueType.Virtual);

        await using var context = _db.CreateContext();
        var venue = await context.Venues.AsNoTracking().SingleAsync(v => v.Id == id);
        venue.Latitude.Should().BeNull();
        venue.Longitude.Should().BeNull();
        venue.TimeZoneId.Should().BeNull();
    }

    [Fact]
    public async Task Venue_StoresBoundaryCoordinatesWithSixDecimals()
    {
        var id = await AddVenueAsync(v => { v.Latitude = -90m; v.Longitude = 180m; });
        var precise = await AddVenueAsync(v => { v.Latitude = 41.878113m; v.Longitude = -87.629799m; });

        await using var context = _db.CreateContext();
        var edge = await context.Venues.AsNoTracking().SingleAsync(v => v.Id == id);
        edge.Latitude.Should().Be(-90m);
        edge.Longitude.Should().Be(180m);
        var chicago = await context.Venues.AsNoTracking().SingleAsync(v => v.Id == precise);
        chicago.Latitude.Should().Be(41.878113m);
        chicago.Longitude.Should().Be(-87.629799m);
    }

    [Theory]
    [InlineData(90.000001, 0)]
    [InlineData(-90.000001, 0)]
    [InlineData(0, 180.000001)]
    [InlineData(0, -180.000001)]
    public async Task Venue_RejectsOutOfRangeCoordinates(double latitude, double longitude)
    {
        var act = () => AddVenueAsync(v => { v.Latitude = (decimal)latitude; v.Longitude = (decimal)longitude; });

        var exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        var sql = exception.InnerException.Should().BeOfType<SqlException>().Which;
        sql.Number.Should().Be(CheckConstraintViolation);
        sql.Message.Should().Contain(latitude is > 90 or < -90 ? "CK_Venues_Latitude_Range" : "CK_Venues_Longitude_Range");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(90)]
    [InlineData(10080)]
    public async Task EventSeries_AcceptsDurationsWithinRange(int minutes)
    {
        var id = await AddSeriesAsync(s => s.DurationMinutes = minutes);

        await using var context = _db.CreateContext();
        (await context.EventSeries.AsNoTracking().SingleAsync(s => s.Id == id)).DurationMinutes.Should().Be(minutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(10081)]
    public async Task EventSeries_RejectsDurationsOutOfRange(int minutes)
    {
        var act = () => AddSeriesAsync(s => s.DurationMinutes = minutes);

        var exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        var sql = exception.InnerException.Should().BeOfType<SqlException>().Which;
        sql.Number.Should().Be(CheckConstraintViolation);
        sql.Message.Should().Contain("CK_EventSeries_DurationMinutes_Range");
    }

    [Fact]
    public async Task EventSeries_RoundTripsLocalTimeDatesAndTimeZone()
    {
        var id = await AddSeriesAsync(s =>
        {
            s.LocalStartTime = new TimeOnly(19, 45);
            s.TimeZoneId = "America/Argentina/ComodRivadavia";
            s.SeriesStartDate = new DateOnly(2026, 11, 1);
            s.SeriesEndDate = new DateOnly(2027, 3, 31);
        });

        await using var context = _db.CreateContext();
        var series = await context.EventSeries.AsNoTracking().SingleAsync(s => s.Id == id);
        series.LocalStartTime.Should().Be(new TimeOnly(19, 45));
        series.TimeZoneId.Should().Be("America/Argentina/ComodRivadavia");
        series.SeriesStartDate.Should().Be(new DateOnly(2026, 11, 1));
        series.SeriesEndDate.Should().Be(new DateOnly(2027, 3, 31));
        series.Status.Should().Be(EventSeriesStatus.Active);
    }

    [Fact]
    public async Task DeletingHostPlayer_KeepsOccurrenceAndSeries_AndClearsHost()
    {
        var hostId = await AddPlayerAsync();
        var seriesId = await AddSeriesAsync(s => s.HostId = hostId);
        var occurrenceId = await AddOccurrenceAsync(o => { o.HostId = hostId; o.SeriesId = seriesId; });

        await ExecuteAsync($"DELETE FROM [Players] WHERE [Id] = '{hostId}'");

        await using var context = _db.CreateContext();
        var occurrence = await context.Events.AsNoTracking().SingleAsync(e => e.Id == occurrenceId);
        occurrence.HostId.Should().BeNull();
        occurrence.SeriesId.Should().Be(seriesId);
        (await context.EventSeries.AsNoTracking().SingleAsync(s => s.Id == seriesId)).HostId.Should().BeNull();
    }

    [Fact]
    public async Task DeletingTeam_KeepsOccurrenceAndSeries_AndClearsTeam()
    {
        var ownerId = await AddPlayerAsync();
        var teamId = await AddTeamAsync(ownerId);
        var seriesId = await AddSeriesAsync(s => s.TeamId = teamId);
        var occurrenceId = await AddOccurrenceAsync(o => { o.TeamId = teamId; o.SeriesId = seriesId; });

        await ExecuteAsync($"DELETE FROM [Teams] WHERE [Id] = '{teamId}'");

        await using var context = _db.CreateContext();
        (await context.Events.AsNoTracking().SingleAsync(e => e.Id == occurrenceId)).TeamId.Should().BeNull();
        (await context.EventSeries.AsNoTracking().SingleAsync(s => s.Id == seriesId)).TeamId.Should().BeNull();
    }

    [Fact]
    public async Task DeletingSeries_NeverDeletesOccurrencesOrTheirRosters()
    {
        var playerId = await AddPlayerAsync();
        var seriesId = await AddSeriesAsync();
        var first = await AddOccurrenceAsync(o => { o.SeriesId = seriesId; o.OccurrenceIndex = 0; });
        var second = await AddOccurrenceAsync(o => { o.SeriesId = seriesId; o.OccurrenceIndex = 1; o.IsDetached = true; o.ScheduledStartUtc = o.ScheduledStartUtc.AddDays(7); });
        var rosterId = await AddRosterEntryAsync(first, playerId);

        await ExecuteAsync($"DELETE FROM [EventSeries] WHERE [Id] = '{seriesId}'");

        await using var context = _db.CreateContext();
        var survivors = await context.Events.AsNoTracking().Where(e => e.Id == first || e.Id == second).ToListAsync();
        survivors.Should().HaveCount(2);
        survivors.Should().OnlyContain(e => e.SeriesId == null);
        survivors.Single(e => e.Id == second).OccurrenceIndex.Should().Be(1);
        survivors.Single(e => e.Id == second).IsDetached.Should().BeTrue();
        (await context.RosterEntries.AsNoTracking().SingleAsync(r => r.Id == rosterId)).EventId.Should().Be(first);
    }

    [Fact]
    public async Task DeletingVenueInUseByOccurrence_IsRefused_AndKeepsTheAssociation()
    {
        var venueId = await AddVenueAsync();
        var occurrenceId = await AddOccurrenceAsync(o => o.VenueId = venueId);

        var act = () => ExecuteAsync($"DELETE FROM [Venues] WHERE [Id] = '{venueId}'");

        (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("FK_Events_Venues_VenueId");
        await using var context = _db.CreateContext();
        (await context.Events.AsNoTracking().SingleAsync(e => e.Id == occurrenceId)).VenueId.Should().Be(venueId);
    }

    [Fact]
    public async Task DeletingVenueInUseBySeries_IsRefused()
    {
        var venueId = await AddVenueAsync();
        await AddSeriesAsync(s => s.VenueId = venueId);

        var act = () => ExecuteAsync($"DELETE FROM [Venues] WHERE [Id] = '{venueId}'");

        (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("FK_EventSeries_Venues_VenueId");
    }

    [Fact]
    public async Task DeletingUnusedVenue_Succeeds()
    {
        var venueId = await AddVenueAsync();

        await ExecuteAsync($"DELETE FROM [Venues] WHERE [Id] = '{venueId}'");

        await using var context = _db.CreateContext();
        (await context.Venues.AnyAsync(v => v.Id == venueId)).Should().BeFalse();
    }

    [Fact]
    public async Task OccurrenceWithVenue_LoadsVenueForDisplayLocation()
    {
        var venueId = await AddVenueAsync(v => v.Name = "Northside Rec Center");
        var occurrenceId = await AddOccurrenceAsync(o => { o.VenueId = venueId; o.LegacyLocation = "old text"; });

        await using var context = _db.CreateContext();
        var occurrence = await context.Events.AsNoTracking().Include(e => e.Venue).SingleAsync(e => e.Id == occurrenceId);
        occurrence.DisplayLocation.Should().Be("Northside Rec Center");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> AddPlayerAsync()
    {
        await using var context = _db.CreateContext();
        var player = new Player { Id = Guid.NewGuid(), Username = $"p_{Guid.NewGuid():N}" };
        context.Players.Add(player);
        await context.SaveChangesAsync();
        return player.Id;
    }

    private async Task<Guid> AddTeamAsync(Guid ownerId)
    {
        await using var context = _db.CreateContext();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"t_{Guid.NewGuid():N}",
            OwnerId = ownerId,
            MaxMembers = 10,
            LifecycleStatus = TeamLifecycleStatus.Active,
            IsAcceptingMembers = true
        };
        context.Teams.Add(team);
        await context.SaveChangesAsync();
        return team.Id;
    }

    private async Task<Guid> AddVenueAsync(Action<Venue>? configure = null)
    {
        await using var context = _db.CreateContext();
        var venue = new Venue { Id = Guid.NewGuid(), Name = "Venue", VenueType = VenueType.Indoor };
        configure?.Invoke(venue);
        context.Venues.Add(venue);
        await context.SaveChangesAsync();
        return venue.Id;
    }

    private async Task<Guid> AddSeriesAsync(Action<EventSeries>? configure = null)
    {
        await using var context = _db.CreateContext();
        var series = new EventSeries
        {
            Id = Guid.NewGuid(),
            Name = "Weekly run",
            LocalStartTime = new TimeOnly(18, 0),
            DurationMinutes = 120,
            MaxParticipants = 20,
            TimeZoneId = "America/Chicago",
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=TU",
            SeriesStartDate = new DateOnly(2026, 11, 3),
            Status = EventSeriesStatus.Active
        };
        configure?.Invoke(series);
        context.EventSeries.Add(series);
        await context.SaveChangesAsync();
        return series.Id;
    }

    private async Task<Guid> AddOccurrenceAsync(Action<EventOccurrence>? configure = null)
    {
        await using var context = _db.CreateContext();
        var occurrence = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = "Occurrence",
            ScheduledStartUtc = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc),
            Status = EventStatus.Planned,
            MaxParticipants = 10
        };
        configure?.Invoke(occurrence);
        context.Events.Add(occurrence);
        await context.SaveChangesAsync();
        return occurrence.Id;
    }

    private async Task<Guid> AddRosterEntryAsync(Guid eventId, Guid playerId)
    {
        await using var context = _db.CreateContext();
        var entry = new RosterEntry { Id = Guid.NewGuid(), EventId = eventId, PlayerId = playerId, RegisteredAtUtc = DateTime.UtcNow };
        context.RosterEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry.Id;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
