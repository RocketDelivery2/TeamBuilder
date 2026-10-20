using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Services;
using TeamBuilder.Tests.Application;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// The integration API as deployed (QA, Swagger off) on real SQL Server: the integration OpenAPI
/// document is still published while the app's own document is not, and a key's reads
/// (authentication with its last-use write, the hosted-game window, the roster) translate and
/// behave on SQL Server, including after the key is revoked.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IntegrationApiSqlServerIntegrationTests(SqlServerContainerFixture fixture)
{
    [Fact]
    public async Task DeployedApi_PublishesOnlyTheIntegrationDocument()
    {
        await using var database = new SqlServerTestDatabase(fixture, "integ_openapi");
        await database.MigrateToAsync();
        await using var factory = new DeployedEnvironmentWebApplicationFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var published = await client.GetAsync("/api/integration/v1/openapi.json");

        published.StatusCode.Should().Be(HttpStatusCode.OK);
        (await published.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths").TryGetProperty("/api/integration/v1/occurrences", out _).Should().BeTrue();
        (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the app's own API document stays a Development convenience");
    }

    [Fact]
    public async Task HostKey_ReadsItsGamesAndRoster_OnSqlServer_UntilRevoked()
    {
        await using var database = new SqlServerTestDatabase(fixture, "integ_roster");
        await database.MigrateToAsync();
        var (hostId, gameId, key, keyId) = await SeedAsync(database);
        await using var factory = new DeployedEnvironmentWebApplicationFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var me = await GetAsync(client, key, "/api/integration/v1/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        (await me.Content.ReadFromJsonAsync<IntegrationCallerDto>())!.PlayerId.Should().Be(hostId);
        await using (var context = database.CreateContext())
            (await context.IntegrationApiKeys.SingleAsync(k => k.Id == keyId)).LastUsedAtUtc.Should().NotBeNull();

        var list = await GetAsync(client, key, "/api/integration/v1/occurrences?from=2027-05-01T00:00:00Z&to=2027-05-31T00:00:00Z");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await list.Content.ReadFromJsonAsync<PaginatedResult<IntegrationOccurrenceDto>>())!;
        page.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = gameId, RequiredCount = 10, SupplyCount = 9, OpenQuantity = 1, IsRosterReady = false, Status = "open"
        });

        var roster = (await (await GetAsync(client, key, $"/api/integration/v1/occurrences/{gameId}/roster")).Content.ReadFromJsonAsync<IntegrationRosterDto>())!;
        roster.Participants.Should().HaveCount(9);
        roster.Participants.Select(p => p.Username).Should().BeInAscendingOrder("join order follows the seeded creation times");
        roster.Requirements.Should().ContainSingle().Which.OpenQuantity.Should().Be(1);

        await using (var context = database.CreateContext())
        {
            var stored = await context.IntegrationApiKeys.SingleAsync(k => k.Id == keyId);
            stored.RevokedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
        (await GetAsync(client, key, "/api/integration/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<(Guid HostId, Guid GameId, string Key, Guid KeyId)> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateContext();
        var now = DateTime.UtcNow;
        var host = new Player { Id = Guid.NewGuid(), Username = "host", CreatedAtUtc = now };
        var game = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = "Thursday Basketball",
            Category = "basketball",
            ScheduledStartUtc = new DateTime(2027, 5, 7, 0, 0, 0, DateTimeKind.Utc),
            Status = EventStatus.Open,
            MaxParticipants = 10,
            HostId = host.Id,
            CreatedAtUtc = now
        };
        var requirement = new RosterRequirement { Id = Guid.NewGuid(), OccurrenceId = game.Id, RoleCode = "participant", RequiredCount = 10, CreatedAtUtc = now };
        context.AddRange(host, game, requirement);
        for (var i = 1; i <= 10; i++)
        {
            var player = new Player { Id = Guid.NewGuid(), Username = $"tester-{i:00}", CreatedAtUtc = now };
            context.Add(player);
            context.Add(new RosterAssignment
            {
                Id = Guid.NewGuid(),
                OccurrenceId = game.Id,
                PlayerId = player.Id,
                RequirementId = requirement.Id,
                RoleCode = "participant",
                Status = i == 10 ? RosterAssignmentStatus.Cancelled : RosterAssignmentStatus.Confirmed,
                Source = RosterAssignmentSource.Player,
                ReservedAtUtc = now.AddMinutes(i),
                ConfirmedAtUtc = now.AddMinutes(i),
                DepartedAtUtc = i == 10 ? now.AddMinutes(30) : null,
                ExitReason = i == 10 ? RosterExitReason.PlayerLeft : null,
                CreatedAtUtc = now.AddMinutes(i)
            });
        }

        var (key, prefix) = IntegrationApiKeyFormat.Generate();
        var apiKey = new IntegrationApiKey
        {
            Id = Guid.NewGuid(),
            PlayerId = host.Id,
            Name = "League site",
            KeyPrefix = prefix,
            SecretHash = IntegrationApiKeyFormat.Hash(key),
            Scopes = IntegrationScopes.RostersRead,
            CreatedAtUtc = now
        };
        context.Add(apiKey);
        await context.SaveChangesAsync();
        return (host.Id, game.Id, key, apiKey.Id);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string key, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        return client.SendAsync(request);
    }
}
