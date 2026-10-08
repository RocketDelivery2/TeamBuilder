using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Data.Configurations;
using TeamBuilder.Infrastructure.Services;
using TeamBuilder.Tests.Application;
using Xunit.Abstractions;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Discovery on a dense, realistic SQL Server 2022 data set (thousands of venues, recurring
/// and one-off games, full and open rosters with history): the key query's actual execution
/// plan seeks <c>SIX_Venues_SearchLocation</c> and <c>IX_Events_VenueId_Status_ScheduledStartUtc</c>,
/// walking every page reproduces an independent ordered query exactly (no duplicates, no
/// skips), a page costs four round trips whatever its size, no identity column is read and no
/// roster history is loaded. Timings are written to the test output as observations only.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LocalDiscoveryScaleSqlServerIntegrationTests : IAsyncLifetime
{
    private const int VenueCount = 20000;
    private const int GamesPerVenue = 2;

    /// <summary>
    /// Seeded once per test run and shared by every test here (all of them only read); the
    /// container's disposal removes it.
    /// </summary>
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static (SqlServerTestDatabase Db, DateTime From)? _shared;
    private static readonly string ChicagoLat = DiscoverySeeding.ChicagoLat.ToString(CultureInfo.InvariantCulture);
    private static readonly string ChicagoLon = DiscoverySeeding.ChicagoLon.ToString(CultureInfo.InvariantCulture);

    private readonly SqlServerContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private SqlServerTestDatabase _db = null!;
    private DateTime _from;

    public LocalDiscoveryScaleSqlServerIntegrationTests(SqlServerContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_shared is null)
            {
                _db = new SqlServerTestDatabase(_fixture, "discoveryscale");
                await _db.MigrateToAsync();
                _from = DateTime.UtcNow.Date.AddDays(1);
                var stopwatch = Stopwatch.StartNew();
                await SeedAsync();
                _output.WriteLine($"Seeded {VenueCount} venues, {VenueCount * GamesPerVenue} games in {stopwatch.ElapsedMilliseconds} ms");
                _shared = (_db, _from);
            }

            (_db, _from) = _shared.Value;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(15, "basketball", false, false)]
    [InlineData(25, null, false, false)]
    [InlineData(50, "basketball", true, false)]
    [InlineData(50, "basketball", true, true)]
    public async Task KeyQuery_ActualPlan_SeeksTheSpatialAndVenueIndexes(double radius, string? activity, bool openOnly, bool withCursor)
    {
        var query = Query(radius, activity, openOnly, pageSize: 20);
        DiscoveryCursor? cursor = null;
        if (withCursor)
        {
            await using var context = _db.CreateContext();
            var first = await new OccurrenceDiscoveryService(context).DiscoverAsync(query, null);
            cursor = DiscoveryCursor.TryParse(first.NextCursor!);
        }

        var (sql, parameters) = OccurrenceDiscoveryService.BuildKeyQuery(query, _from, _from.AddDays(7), cursor);

        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using (var on = connection.CreateCommand())
        {
            on.CommandText = "SET STATISTICS XML ON";
            await on.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters.Select(p => (object)((ICloneable)p).Clone()).ToArray());
        var stopwatch = Stopwatch.StartNew();
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
            rows++;
        await reader.NextResultAsync();
        await reader.ReadAsync();
        var plan = reader.GetString(0);
        stopwatch.Stop();

        rows.Should().BeInRange(1, 21);
        plan.Should().Contain($"Index=\"[{VenueConfiguration.SearchLocationIndexName}]\"", "the radius predicate is answered by the spatial index");
        plan.Should().Contain($"Index=\"[{EventOccurrenceConfiguration.VenueStatusStartIndexName}]\"", "each venue's live games are a seek");
        plan.Should().NotContain("PhysicalOp=\"Clustered Index Scan\"", "no table is scanned");
        plan.Should().NotContain("PhysicalOp=\"Table Scan\"");
        if (openOnly)
        {
            plan.Should().Contain("Index=\"[IX_RosterAssignments_RequirementId_OccurrenceId]\"");
            plan.Should().NotContain("Index=\"[PK_RosterAssignments]\"", "the supply count is answered from the covering index, with no key lookups");
        }
        _output.WriteLine($"radius {radius}, activity {activity ?? "any"}, openOnly {openOnly}, cursor {withCursor}: {rows} rows in {stopwatch.ElapsedMilliseconds} ms (observation)");
    }

    [Theory]
    [InlineData(25, false, 50)]
    [InlineData(25, true, 37)]
    public async Task WalkingEveryPage_MatchesAnIndependentOrderedQuery_ExactlyOnce(double radius, bool openOnly, int pageSize)
    {
        var expected = await ReferenceAsync(radius, openOnly);
        expected.Count.Should().BeGreaterThan(pageSize * 3, "the walk must span several pages");

        var walked = new List<DiscoveredOccurrenceDto>();
        string? cursor = null;
        var pages = 0;
        var stopwatch = Stopwatch.StartNew();
        do
        {
            await using var context = _db.CreateContext();
            var page = await new OccurrenceDiscoveryService(context).DiscoverAsync(Query(radius, "basketball", openOnly, pageSize, cursor), null);
            page.Items.Count.Should().BeLessThanOrEqualTo(pageSize);
            walked.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null);
        stopwatch.Stop();

        walked.Select(i => i.OccurrenceId).Should().OnlyHaveUniqueItems();
        walked.Select(i => i.OccurrenceId).Should().Equal(expected);
        if (openOnly)
            walked.Should().OnlyContain(i => i.Roster.TotalOpenQuantity > 0);
        _output.WriteLine($"radius {radius}, openOnly {openOnly}: {walked.Count} games over {pages} pages of {pageSize} in {stopwatch.ElapsedMilliseconds} ms, {stopwatch.ElapsedMilliseconds / pages} ms/page (observation)");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public async Task APage_IsFourRoundTrips_ReadsNoIdentityColumns_AndNoHistory(int pageSize)
    {
        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(_db.ConnectionString)
            .AddInterceptors(recorder)
            .Options;
        await using var context = new TeamBuilderDbContext(options);
        var viewer = await context.Players.Select(p => p.Id).FirstAsync();
        recorder.Commands.Clear();

        var page = await new OccurrenceDiscoveryService(context).DiscoverAsync(Query(50, "basketball", false, pageSize), viewer);

        page.Items.Should().HaveCount(pageSize);
        recorder.Commands.Should().HaveCount(4);
        recorder.Commands[0].Should().Contain("STDistance");
        foreach (var forbidden in new[] { "Email", "Subject", "Issuer", "Provider", "TenantId", "PlayerIdentities", "Username", "DisplayName" })
            recorder.Commands.Should().NotContain(c => c.Contains($"[{forbidden}]"), forbidden);
        recorder.Commands[3].Should().Contain("FROM [RosterAssignments]").And.Contain("[Status]", "only current supply rows are read, never history");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private DiscoverOccurrencesQuery Query(double radius, string? activity, bool openOnly, int pageSize, string? cursor = null) =>
        new(DiscoverySeeding.ChicagoLat, DiscoverySeeding.ChicagoLon, radius, activity, _from, _from.AddDays(7), openOnly, pageSize, cursor);

    /// <summary>The expected order, straight from SQL Server without TOP or a cursor.</summary>
    private async Task<List<Guid>> ReferenceAsync(double radius, bool openOnly)
    {
        var meters = (radius * 1609.344).ToString("R", CultureInfo.InvariantCulture);
        var open = openOnly
            ? """
              AND EXISTS (SELECT 1 FROM [RosterRequirements] r WHERE r.[OccurrenceId] = e.[Id]
                AND r.[RequiredCount] > (SELECT COUNT(*) FROM [RosterAssignments] a WHERE a.[RequirementId] = r.[Id] AND a.[Status] IN (1, 2, 3, 4)))
              """
            : "";
        var sql = $"""
            SELECT e.[Id]
            FROM [Events] e JOIN [Venues] v ON v.[Id] = e.[VenueId]
            CROSS APPLY (SELECT v.[SearchLocation].STDistance(geography::Point({ChicagoLat}, {ChicagoLon}, 4326)) AS [D]) d
            WHERE d.[D] <= {meters} AND e.[Status] IN (1, 2, 3) AND e.[Category] = N'basketball'
              AND e.[ScheduledStartUtc] >= @from AND e.[ScheduledStartUtc] < @to {open}
            ORDER BY d.[D], e.[ScheduledStartUtc], e.[Id]
            """;
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@from", System.Data.SqlDbType.DateTime2) { Value = _from });
        command.Parameters.Add(new SqlParameter("@to", System.Data.SqlDbType.DateTime2) { Value = _from.AddDays(7) });
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// Set-based seed: 60% of venues in a dense metro box around Chicago, the rest across the
    /// continental US; 1 in 5 Private, 1 in 50 Virtual, 1 in 40 without coordinates. Two games
    /// per venue over two weeks, one in five recurring (series-generated), mostly basketball,
    /// some closed; every game needs 10 participants (every 7th also a referee) with 0..10
    /// current players, and every third game carries a Cancelled history row.
    /// </summary>
    private async Task SeedAsync()
    {
        var from = _from.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var sql = $"""
            SET NOCOUNT ON;
            WITH n AS (SELECT TOP ({VenueCount}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            SELECT i, NEWID() AS VenueId INTO #v FROM n;

            INSERT INTO [Venues] ([Id], [Name], [City], [StateOrProvince], [CountryCode], [AddressLine1], [Latitude], [Longitude], [TimeZoneId], [VenueType], [PrivacyLevel], [CreatedAtUtc])
            SELECT VenueId, CONCAT(N'Court ', i), N'Chicago', N'IL', N'US', CONCAT(i, N' Main St'),
                CASE WHEN i % 40 = 39 THEN NULL WHEN i % 5 < 3 THEN 41.5 + (ABS(CHECKSUM(VenueId)) % 80000) / 100000.0 ELSE 25 + (ABS(CHECKSUM(VenueId)) % 2300000) / 100000.0 END,
                CASE WHEN i % 40 = 39 THEN NULL WHEN i % 5 < 3 THEN -88.3 + (ABS(CHECKSUM(REVERSE(CAST(VenueId AS nvarchar(36))))) % 100000) / 100000.0 ELSE -124 + (ABS(CHECKSUM(REVERSE(CAST(VenueId AS nvarchar(36))))) % 5500000) / 100000.0 END,
                N'America/Chicago', CASE WHEN i % 50 = 49 THEN 3 ELSE 1 END, CASE WHEN i % 5 = 4 THEN 2 ELSE 1 END, SYSUTCDATETIME()
            FROM #v;

            SELECT i, VenueId, NEWID() AS SeriesId INTO #s FROM #v WHERE i % 5 = 0;
            INSERT INTO [EventSeries] ([Id], [Name], [RecurrenceRule], [TimeZoneId], [LocalStartTime], [DurationMinutes], [SeriesStartDate], [MaxParticipants], [Status], [VenueId], [Category], [CreatedAtUtc])
            SELECT SeriesId, CONCAT(N'Weekly run ', i), N'FREQ=WEEKLY;BYDAY=WE', N'America/Chicago', '20:00', 120, CAST('{from}' AS date), 10, 1, VenueId, N'basketball', SYSUTCDATETIME() FROM #s;

            SELECT NEWID() AS EventId, v.i * {GamesPerVenue} + g.k AS j, v.VenueId, s.SeriesId, g.k INTO #e
            FROM #v v CROSS JOIN (SELECT TOP ({GamesPerVenue}) k FROM (VALUES (0), (1), (2), (3)) x(k) ORDER BY k) g LEFT JOIN #s s ON s.VenueId = v.VenueId;

            INSERT INTO [Events] ([Id], [Name], [Category], [ScheduledStartUtc], [ScheduledEndUtc], [Status], [MaxParticipants], [CurrentParticipantCount], [IsDetached], [VenueId], [SeriesId], [OccurrenceIndex], [CreatedAtUtc])
            SELECT EventId, CONCAT(N'Game ', j), CASE WHEN j % 6 = 5 THEN N'soccer' ELSE N'basketball' END,
                DATEADD(minute, (j * 37) % (60 * 24 * 14), '{from}'), DATEADD(minute, (j * 37) % (60 * 24 * 14) + 120, '{from}'),
                CASE WHEN j % 13 = 12 THEN 5 WHEN j % 2 = 0 THEN 2 ELSE 1 END, 10, 0, 0, VenueId,
                CASE WHEN SeriesId IS NOT NULL THEN SeriesId END, CASE WHEN SeriesId IS NOT NULL THEN k END, SYSUTCDATETIME()
            FROM #e;

            SELECT NEWID() AS RequirementId, EventId, j INTO #r FROM #e;
            INSERT INTO [RosterRequirements] ([Id], [OccurrenceId], [RoleCode], [RequiredCount], [CreatedAtUtc])
            SELECT RequirementId, EventId, N'participant', 10, SYSUTCDATETIME() FROM #r;
            INSERT INTO [RosterRequirements] ([Id], [OccurrenceId], [RoleCode], [RequiredCount], [CreatedAtUtc])
            SELECT NEWID(), EventId, N'referee', 1, SYSUTCDATETIME() FROM #e WHERE j % 7 = 0;

            WITH p AS (SELECT TOP (11) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS slot FROM sys.all_objects)
            SELECT slot, NEWID() AS PlayerId INTO #p FROM p;
            INSERT INTO [Players] ([Id], [Username], [Email], [CreatedAtUtc])
            SELECT PlayerId, CONCAT(N'scale-', slot, N'-', LEFT(CAST(NEWID() AS nvarchar(36)), 8)), CONCAT(N'scale', slot, N'@example.test'), SYSUTCDATETIME() FROM #p;

            INSERT INTO [RosterAssignments] ([Id], [OccurrenceId], [PlayerId], [RequirementId], [RoleCode], [Status], [Source], [CreatedAtUtc])
            SELECT NEWID(), r.EventId, p.PlayerId, r.RequirementId, N'participant', 2, 1, SYSUTCDATETIME()
            FROM #r r JOIN #p p ON p.slot < r.j % 11;

            INSERT INTO [RosterAssignments] ([Id], [OccurrenceId], [PlayerId], [RequirementId], [RoleCode], [Status], [Source], [ExitReason], [CreatedAtUtc])
            SELECT NEWID(), r.EventId, p.PlayerId, r.RequirementId, N'participant', 7, 1, 1, SYSUTCDATETIME()
            FROM #r r JOIN #p p ON p.slot = 10 WHERE r.j % 3 = 0;

            UPDATE STATISTICS [Venues];
            UPDATE STATISTICS [Events];
            UPDATE STATISTICS [RosterRequirements];
            UPDATE STATISTICS [RosterAssignments];
            """;
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
