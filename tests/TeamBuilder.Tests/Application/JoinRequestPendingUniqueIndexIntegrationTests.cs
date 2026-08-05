using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Persistence;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves the UX_JoinRequests_TeamId_PlayerId_Pending filtered unique index behaves as
/// intended against a real SQL Server: only one pending join request per (TeamId, PlayerId)
/// pair, historical approved/rejected/cancelled requests never block a new pending request,
/// and other teams/players are unaffected.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class JoinRequestPendingUniqueIndexIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public JoinRequestPendingUniqueIndexIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "jrindex");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task FirstPendingRequest_ForPair_Succeeds()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();

        await using var context = _db.CreateContext();
        context.JoinRequests.Add(NewRequest(teamId, playerId, RequestStatus.Pending));

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SecondDirectPendingRequest_ForSamePair_FailsAtSqlServer_WithRecognizedConflict()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();

        await using (var first = _db.CreateContext())
        {
            first.JoinRequests.Add(NewRequest(teamId, playerId, RequestStatus.Pending));
            await first.SaveChangesAsync();
        }

        // A second, independent context simulates a concurrent/direct insert - only the real
        // unique index (not EF's change tracker) can prevent the duplicate.
        await using var second = _db.CreateContext();
        second.JoinRequests.Add(NewRequest(teamId, playerId, RequestStatus.Pending));

        var act = async () => await second.SaveChangesAsync();

        var assertion = await act.Should().ThrowAsync<DbUpdateException>();
        var dbUpdateException = assertion.Which;

        var sqlException = dbUpdateException.InnerException as SqlException;
        sqlException.Should().NotBeNull("the failure must be a real SQL Server duplicate-key error");
        sqlException!.Number.Should().BeOneOf(2601, 2627);
        sqlException.Message.Should().Contain("UX_JoinRequests_TeamId_PlayerId_Pending");

        JoinRequestConflictClassifier.IsDuplicatePendingJoinRequest(dbUpdateException).Should().BeTrue();
    }

    [Theory]
    [InlineData(RequestStatus.Approved)]
    [InlineData(RequestStatus.Rejected)]
    [InlineData(RequestStatus.Cancelled)]
    public async Task HistoricalNonPendingRequest_PlusOneNewPendingRequest_ForSamePair_Succeeds(RequestStatus historicalStatus)
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();

        await using var context = _db.CreateContext();
        context.JoinRequests.Add(NewRequest(teamId, playerId, historicalStatus));
        context.JoinRequests.Add(NewRequest(teamId, playerId, RequestStatus.Pending));

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task PendingRequest_ForSamePlayer_OnADifferentTeam_IsNotBlocked()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();
        var (otherTeamId, _) = await SeedTeamAndPlayerAsync();

        await using var context = _db.CreateContext();
        context.JoinRequests.Add(NewRequest(teamId, playerId, RequestStatus.Pending));
        context.JoinRequests.Add(NewRequest(otherTeamId, playerId, RequestStatus.Pending));

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task PendingRequest_ForSameTeam_WithADifferentPlayer_IsNotBlocked()
    {
        var (teamId, playerId) = await SeedTeamAndPlayerAsync();
        var (_, otherPlayerId) = await SeedTeamAndPlayerAsync();

        await using var context = _db.CreateContext();
        context.JoinRequests.Add(NewRequest(teamId, playerId, RequestStatus.Pending));
        context.JoinRequests.Add(NewRequest(teamId, otherPlayerId, RequestStatus.Pending));

        await context.SaveChangesAsync();
    }

    private async Task<(Guid TeamId, Guid PlayerId)> SeedTeamAndPlayerAsync()
    {
        await using var context = _db.CreateContext();

        var player = new Player { Id = Guid.NewGuid(), Username = $"player_{Guid.NewGuid():N}" };
        var team = new Team { Id = Guid.NewGuid(), Name = $"team_{Guid.NewGuid():N}", MaxMembers = 100 };

        context.Players.Add(player);
        context.Teams.Add(team);
        await context.SaveChangesAsync();

        return (team.Id, player.Id);
    }

    private static JoinRequest NewRequest(Guid teamId, Guid playerId, RequestStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = teamId,
        PlayerId = playerId,
        Status = status,
        RequestedAtUtc = DateTime.UtcNow,
        ProcessedAtUtc = status == RequestStatus.Pending ? null : DateTime.UtcNow
    };
}
