using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Tests.Application;

namespace TeamBuilder.Tests.Integration;

/// <summary>Hosts the API with a fixed clock: "now" is Monday 2026-10-05 12:00 UTC.</summary>
public sealed class EventSeriesApiFixture : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly TeamBuilderWebApplicationFactory _root = new();

    public EventSeriesApiFixture()
    {
        App = _root.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock)));
    }

    internal FixedTimeProvider Clock { get; } = new(Now);

    public WebApplicationFactory<Program> App { get; }

    public void Dispose()
    {
        App.Dispose();
        _root.Dispose();
    }
}

public sealed class EventSeriesControllerIntegrationTests : IClassFixture<EventSeriesApiFixture>
{
    private const string BaseUrl = "/api/v1/event-series";

    // Tuesday after the fixed "now".
    private static readonly DateOnly NextTuesday = new(2026, 10, 13);

    private readonly EventSeriesApiFixture _fixture;
    private readonly HttpClient _client;

    public EventSeriesControllerIntegrationTests(EventSeriesApiFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.App.CreateClient();
    }

    // ── create / auth ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Unauthenticated_Returns401()
    {
        var response = await _client.PostAsJsonAsync(BaseUrl, Body());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_UnlinkedIdentity_Returns403_AndCreatesNothing()
    {
        var before = await CountSeriesAsync();

        var response = await SendAsync(HttpMethod.Post, BaseUrl, LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()), Body());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountSeriesAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Create_Standalone_Returns201_WithResolvedHost_AndMaterializesTheTuesdayExample()
    {
        var hostId = Guid.NewGuid();

        var response = await PostAsPlayerAsync(hostId, Body(name: "Tuesday hoops", maxParticipants: 14));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var series = (await response.Content.ReadFromJsonAsync<EventSeriesDto>())!;
        response.Headers.Location!.AbsolutePath.Should().Be($"{BaseUrl}/{series.Id}");
        series.HostId.Should().Be(hostId);
        series.TeamId.Should().BeNull();
        series.Status.Should().Be(EventSeriesStatus.Active);
        series.TimeZoneId.Should().Be("America/New_York");
        series.LocalStartTime.Should().Be(new TimeOnly(17, 0));
        series.RecurrenceRule.Should().Be("FREQ=WEEKLY;BYDAY=TU");
        series.SeriesStartDate.Should().Be(NextTuesday);
        series.MaxParticipants.Should().Be(14);

        // Local dates 10-13 .. 11-02 (21 days): Tuesdays 10-13, 10-20, 10-27 at 17:00 EDT.
        var occurrences = await OccurrencesAsync(series.Id);
        occurrences.Select(o => o.ScheduledStartUtc).Should().Equal(
            Utc(2026, 10, 13, 21), Utc(2026, 10, 20, 21), Utc(2026, 10, 27, 21));
        occurrences.Should().OnlyContain(o =>
            o.SeriesId == series.Id && o.HostId == hostId && o.MaxParticipants == 14 &&
            o.Status == EventStatus.Planned && !o.IsDetached && o.CurrentParticipantCount == 0 &&
            o.Name == "Tuesday hoops" && o.ScheduledEndUtc == o.ScheduledStartUtc.AddMinutes(90));
    }

    [Fact]
    public async Task Create_InitialHorizon_StopsAfter21LocalDays()
    {
        var series = await CreateSeriesAsync(Guid.NewGuid(), Body(rrule: "FREQ=DAILY"));

        var occurrences = await OccurrencesAsync(series.Id, pageSize: 100);

        occurrences.Should().HaveCount(21);
        occurrences[^1].ScheduledStartUtc.Should().Be(Utc(2026, 11, 2, 22)); // 17:00 EST, after fall-back
        occurrences.Select(o => o.ScheduledStartUtc).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Create_SeriesEndDate_ShortensTheHorizon()
    {
        var series = await CreateSeriesAsync(Guid.NewGuid(), Body(rrule: "FREQ=DAILY", seriesEndDate: NextTuesday.AddDays(4)));

        (await OccurrencesAsync(series.Id, pageSize: 100)).Should().HaveCount(5);
        series.SeriesEndDate.Should().Be(NextTuesday.AddDays(4));
    }

    [Fact]
    public async Task Create_TeamOwner_Returns201_AndCopiesTeamHostAndVenue()
    {
        var ownerId = Guid.NewGuid();
        var team = await SeedTeamAsync(ownerId);
        var venue = await SeedVenueAsync("America/New_York");

        var response = await PostAsPlayerAsync(ownerId, Body(teamId: team.Id, venueId: venue.Id, timeZoneId: null));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var series = (await response.Content.ReadFromJsonAsync<EventSeriesDto>())!;
        series.TeamId.Should().Be(team.Id);
        series.VenueId.Should().Be(venue.Id);
        var occurrences = await OccurrencesAsync(series.Id);
        occurrences.Should().NotBeEmpty().And.OnlyContain(o =>
            o.TeamId == team.Id && o.HostId == ownerId && o.VenueId == venue.Id && o.Location == venue.Name);
    }

    [Fact]
    public async Task Create_NonOwnerTeamSeries_Returns403()
    {
        var team = await SeedTeamAsync(Guid.NewGuid());

        var response = await PostAsPlayerAsync(Guid.NewGuid(), Body(teamId: team.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_MissingTeam_Returns404()
    {
        var response = await PostAsPlayerAsync(Guid.NewGuid(), Body(teamId: Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(TeamLifecycleStatus.Inactive)]
    [InlineData(TeamLifecycleStatus.Disbanded)]
    public async Task Create_InactiveOrDisbandedTeam_Returns409(TeamLifecycleStatus lifecycle)
    {
        var ownerId = Guid.NewGuid();
        var team = await SeedTeamAsync(ownerId, lifecycle);

        var response = await PostAsPlayerAsync(ownerId, Body(teamId: team.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_MissingVenue_Returns404()
    {
        var response = await PostAsPlayerAsync(Guid.NewGuid(), Body(venueId: Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_EndDateBeforeStartDate_Returns400()
    {
        var response = await PostAsPlayerAsync(Guid.NewGuid(), Body(seriesEndDate: NextTuesday.AddDays(-1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_StartDateBeforeSeriesLocalToday_Returns400_ButTodayIsAllowed()
    {
        // 2026-10-05 12:00Z is still 2026-10-05 in New York.
        (await PostAsPlayerAsync(Guid.NewGuid(), Body(seriesStartDate: new DateOnly(2026, 10, 4))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsPlayerAsync(Guid.NewGuid(), Body(seriesStartDate: new DateOnly(2026, 10, 5))))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_StartDate_IsJudgedInTheSeriesTimeZone()
    {
        // At 12:00Z it is already 2026-10-06 in Kiritimati (UTC+14), so 10-05 is in the past there.
        (await PostAsPlayerAsync(Guid.NewGuid(), Body(seriesStartDate: new DateOnly(2026, 10, 5), timeZoneId: "Pacific/Kiritimati")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("FREQ=MONTHLY")]
    [InlineData("FREQ=DAILY;FREQ=DAILY")]
    [InlineData("FREQ=DAILY;FOO=1")]
    [InlineData("FREQ=WEEKLY;BYDAY=XX")]
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    [InlineData("FREQ=WEEKLY;COUNT=5")]
    [InlineData("FREQ=WEEKLY;UNTIL=20261231T000000Z")]
    [InlineData("")]
    public async Task Create_InvalidRecurrenceRule_Returns400_AndCreatesNothing(string rrule)
    {
        var before = await CountSeriesAsync();

        var response = await PostAsPlayerAsync(Guid.NewGuid(), Body(rrule: rrule));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountSeriesAsync()).Should().Be(before);
    }

    [Theory]
    [InlineData("Eastern Standard Time")]
    [InlineData("Not/A_Zone")]
    [InlineData("america/new_york")]
    public async Task Create_InvalidTimeZone_Returns400(string timeZoneId)
    {
        var response = await PostAsPlayerAsync(Guid.NewGuid(), Body(timeZoneId: timeZoneId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_UtcTimeZone_IsAccepted()
    {
        var series = await CreateSeriesAsync(Guid.NewGuid(), Body(timeZoneId: "UTC", rrule: "FREQ=DAILY", seriesEndDate: NextTuesday));

        series.TimeZoneId.Should().Be("UTC");
        (await OccurrencesAsync(series.Id)).Single().ScheduledStartUtc.Should().Be(Utc(2026, 10, 13, 17));
    }

    [Fact]
    public async Task Create_NoTimeZoneAndNoVenue_Returns400()
    {
        var response = await PostAsPlayerAsync(Guid.NewGuid(), Body(timeZoneId: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_VenueTimeZone_DefaultsTheSeriesTimeZone()
    {
        var venue = await SeedVenueAsync("America/Chicago");

        var series = await CreateSeriesAsync(Guid.NewGuid(), Body(venueId: venue.Id, timeZoneId: null));

        series.TimeZoneId.Should().Be("America/Chicago");
        (await OccurrencesAsync(series.Id))[0].ScheduledStartUtc.Should().Be(Utc(2026, 10, 13, 22)); // 17:00 CDT
    }

    [Fact]
    public async Task Create_VenueTimeZone_EqualRequestTimeZone_IsAccepted()
    {
        var venue = await SeedVenueAsync("America/Chicago");

        (await PostAsPlayerAsync(Guid.NewGuid(), Body(venueId: venue.Id, timeZoneId: "America/Chicago")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_VenueRequestTimeZoneMismatch_Returns400()
    {
        var venue = await SeedVenueAsync("America/Chicago");

        var response = await PostAsPlayerAsync(Guid.NewGuid(), Body(venueId: venue.Id, timeZoneId: "America/New_York"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_VenueWithoutTimeZone_RequiresRequestTimeZone()
    {
        var venue = await SeedVenueAsync(null);

        (await PostAsPlayerAsync(Guid.NewGuid(), Body(venueId: venue.Id, timeZoneId: null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsPlayerAsync(Guid.NewGuid(), Body(venueId: venue.Id, timeZoneId: "Europe/London")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    [InlineData(null)]
    public async Task Create_InvalidMaxParticipants_Returns400(int? maxParticipants)
    {
        var body = Body();
        body["maxParticipants"] = maxParticipants;

        (await PostAsPlayerAsync(Guid.NewGuid(), body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── reads ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSeries_IsPublic_AndReturnsTheTemplate()
    {
        var created = await CreateSeriesAsync(Guid.NewGuid(), Body(description: "Weekly run", category: "Running", tags: "5k"));

        var response = await _client.GetAsync($"{BaseUrl}/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var series = (await response.Content.ReadFromJsonAsync<EventSeriesDto>())!;
        series.Should().BeEquivalentTo(created, o => o.Excluding(s => s.CreatedAtUtc));
        series.Description.Should().Be("Weekly run");
        series.Category.Should().Be("Running");
        series.Tags.Should().Be("5k");
        series.DurationMinutes.Should().Be(90);

        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain("occurrences");
    }

    [Fact]
    public async Task GetSeries_Missing_Returns404()
    {
        (await _client.GetAsync($"{BaseUrl}/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetOccurrences_IsPublic_PagedAndOrderedByStart()
    {
        var series = await CreateSeriesAsync(Guid.NewGuid(), Body(rrule: "FREQ=DAILY"));

        var page1 = await GetOccurrencePageAsync(series.Id, page: 1, pageSize: 5);
        var page2 = await GetOccurrencePageAsync(series.Id, page: 2, pageSize: 5);
        var all = await OccurrencesAsync(series.Id, pageSize: 100);

        page1.TotalCount.Should().Be(21);
        page1.Items.Should().HaveCount(5);
        page1.Items.Select(o => o.Id).Concat(page2.Items.Select(o => o.Id))
            .Should().Equal(all.Take(10).Select(o => o.Id));
        all.Select(o => o.ScheduledStartUtc).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetOccurrences_SerializesScheduledTimesAsUtc()
    {
        var series = await CreateSeriesAsync(Guid.NewGuid(), Body());

        var json = await _client.GetStringAsync($"{BaseUrl}/{series.Id}/occurrences");

        using var document = JsonDocument.Parse(json);
        var first = document.RootElement.GetProperty("items")[0];
        first.GetProperty("scheduledStartUtc").GetString().Should().Be("2026-10-13T21:00:00Z");
        first.GetProperty("scheduledEndUtc").GetString().Should().Be("2026-10-13T22:30:00Z");
        first.GetProperty("eventDateUtc").GetString().Should().Be("2026-10-13T21:00:00Z");
    }

    [Fact]
    public async Task GetOccurrences_MissingSeries_Returns404()
    {
        (await _client.GetAsync($"{BaseUrl}/{Guid.NewGuid()}/occurrences")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_ByHost_CancelsFutureAttachedOccurrences_KeepsPastAndDetached_AndKeepsRows()
    {
        var hostId = Guid.NewGuid();

        // Daily at 09:00 UTC from today: today's 09:00 is already past at the fixed 12:00Z.
        var series = await CreateSeriesAsync(hostId, Body(rrule: "FREQ=DAILY", timeZoneId: "UTC",
            seriesStartDate: new DateOnly(2026, 10, 5), localStartTime: "09:00:00"));
        var before = await OccurrencesAsync(series.Id, pageSize: 100);
        before.Should().HaveCount(21);
        var past = before[0];
        past.ScheduledStartUtc.Should().BeBefore(EventSeriesApiFixture.Now.UtcDateTime);
        var detached = before[3];
        await MarkDetachedAsync(detached.Id);

        var response = await SendAsync(HttpMethod.Delete, $"{BaseUrl}/{series.Id}", await TokenForAsync(hostId));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetSeriesAsync(series.Id)).Status.Should().Be(EventSeriesStatus.Cancelled);
        var after = (await OccurrencesAsync(series.Id, pageSize: 100)).ToDictionary(o => o.Id);
        after.Should().HaveCount(21);
        after[past.Id].Status.Should().Be(EventStatus.Planned);
        after[detached.Id].Status.Should().Be(EventStatus.Planned);
        after.Values.Where(o => o.Id != past.Id && o.Id != detached.Id)
            .Should().OnlyContain(o => o.Status == EventStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_Twice_SecondReturns409()
    {
        var hostId = Guid.NewGuid();
        var series = await CreateSeriesAsync(hostId, Body());
        var token = await TokenForAsync(hostId);

        (await SendAsync(HttpMethod.Delete, $"{BaseUrl}/{series.Id}", token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Delete, $"{BaseUrl}/{series.Id}", token)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cancel_NonHost_Returns403_AndChangesNothing()
    {
        var series = await CreateSeriesAsync(Guid.NewGuid(), Body());

        var response = await SendAsync(HttpMethod.Delete, $"{BaseUrl}/{series.Id}", await TokenForAsync(Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetSeriesAsync(series.Id)).Status.Should().Be(EventSeriesStatus.Active);
        (await OccurrencesAsync(series.Id)).Should().OnlyContain(o => o.Status == EventStatus.Planned);
    }

    [Fact]
    public async Task Cancel_Unlinked_Returns403()
    {
        var series = await CreateSeriesAsync(Guid.NewGuid(), Body());

        var response = await SendAsync(HttpMethod.Delete, $"{BaseUrl}/{series.Id}", LinkedPlayerTokens.ForSubject(LinkedPlayerTokens.NewSubject()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cancel_Unauthenticated_Returns401()
    {
        (await _client.DeleteAsync($"{BaseUrl}/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cancel_Missing_Returns404()
    {
        var response = await SendAsync(HttpMethod.Delete, $"{BaseUrl}/{Guid.NewGuid()}", await TokenForAsync(Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_OrphanSeries_Returns409()
    {
        var seriesId = Guid.NewGuid();
        using (var scope = _fixture.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            var series = SeriesSchedulingTests.NewSeries("UTC", "FREQ=DAILY", new DateOnly(2026, 10, 6), new TimeOnly(9, 0));
            series.Id = seriesId;
            series.HostId = null;
            db.EventSeries.Add(series);
            await db.SaveChangesAsync();
        }

        var response = await SendAsync(HttpMethod.Delete, $"{BaseUrl}/{seriesId}", await TokenForAsync(Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static Dictionary<string, object?> Body(
        string name = "Weekly game",
        string rrule = "FREQ=WEEKLY;BYDAY=TU",
        string? timeZoneId = "America/New_York",
        DateOnly? seriesStartDate = null,
        DateOnly? seriesEndDate = null,
        Guid? teamId = null,
        Guid? venueId = null,
        int maxParticipants = 10,
        string localStartTime = "17:00:00",
        string? description = null,
        string? category = null,
        string? tags = null) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["category"] = category,
        ["tags"] = tags,
        ["teamId"] = teamId,
        ["venueId"] = venueId,
        ["localStartTime"] = localStartTime,
        ["durationMinutes"] = 90,
        ["timeZoneId"] = timeZoneId,
        ["recurrenceRule"] = rrule,
        ["seriesStartDate"] = (seriesStartDate ?? NextTuesday).ToString("yyyy-MM-dd"),
        ["seriesEndDate"] = seriesEndDate?.ToString("yyyy-MM-dd"),
        ["maxParticipants"] = maxParticipants
    };

    private Task<string> TokenForAsync(Guid playerId) => LinkedPlayerTokens.ForPlayerAsync(_fixture.App.Services, playerId);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostAsPlayerAsync(Guid playerId, object body) =>
        await SendAsync(HttpMethod.Post, BaseUrl, await TokenForAsync(playerId), body);

    private async Task<EventSeriesDto> CreateSeriesAsync(Guid hostId, object body)
    {
        var response = await PostAsPlayerAsync(hostId, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<EventSeriesDto>())!;
    }

    private async Task<EventSeriesDto> GetSeriesAsync(Guid id) =>
        (await _client.GetFromJsonAsync<EventSeriesDto>($"{BaseUrl}/{id}"))!;

    private async Task<PaginatedResult<EventDto>> GetOccurrencePageAsync(Guid seriesId, int page, int pageSize) =>
        (await _client.GetFromJsonAsync<PaginatedResult<EventDto>>($"{BaseUrl}/{seriesId}/occurrences?page={page}&pageSize={pageSize}"))!;

    private async Task<List<EventDto>> OccurrencesAsync(Guid seriesId, int pageSize = 20) =>
        (await GetOccurrencePageAsync(seriesId, 1, pageSize)).Items.ToList();

    private async Task<int> CountSeriesAsync()
    {
        using var scope = _fixture.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>().EventSeries.CountAsync();
    }

    private async Task<Team> SeedTeamAsync(Guid ownerId, TeamLifecycleStatus lifecycle = TeamLifecycleStatus.Active)
    {
        using var scope = _fixture.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        if (!await db.Players.AnyAsync(p => p.Id == ownerId))
            db.Players.Add(new Player { Id = ownerId, Username = $"owner-{Guid.NewGuid():N}" });
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"Team {Guid.NewGuid():N}",
            LifecycleStatus = lifecycle,
            IsAcceptingMembers = lifecycle == TeamLifecycleStatus.Active,
            MaxMembers = 10,
            OwnerId = ownerId
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return team;
    }

    private async Task<Venue> SeedVenueAsync(string? timeZoneId)
    {
        using var scope = _fixture.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var venue = new Venue
        {
            Id = Guid.NewGuid(),
            Name = $"Court {Guid.NewGuid():N}",
            TimeZoneId = timeZoneId,
            VenueType = VenueType.Indoor
        };
        db.Venues.Add(venue);
        await db.SaveChangesAsync();
        return venue;
    }

    private async Task MarkDetachedAsync(Guid occurrenceId)
    {
        using var scope = _fixture.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        var occurrence = await db.Events.SingleAsync(e => e.Id == occurrenceId);
        occurrence.IsDetached = true;
        await db.SaveChangesAsync();
    }
}
