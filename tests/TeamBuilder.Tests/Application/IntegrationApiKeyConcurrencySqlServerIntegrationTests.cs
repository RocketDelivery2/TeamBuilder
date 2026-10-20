using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Exceptions;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

/// <summary>Verifies the active API-key limit under concurrent calls against SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class IntegrationApiKeyConcurrencySqlServerIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerTestDatabase _db;

    public IntegrationApiKeyConcurrencySqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _db = new SqlServerTestDatabase(fixture, "apikeylimit");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Create_AtLimitSerializesConcurrentRequestsForOnePlayer()
    {
        await _db.MigrateToAsync();

        var playerId = Guid.NewGuid();
        await using (var context = _db.CreateContext())
        {
            context.Players.Add(new Player
            {
                Id = playerId,
                Username = $"key-limit-{playerId:N}",
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        for (var i = 0; i < IIntegrationApiKeyService.MaxActiveKeysPerPlayer - 1; i++)
        {
            await using var context = _db.CreateContext();
            await Service(context).CreateAsync(playerId, new CreateIntegrationApiKeyDto { Name = $"key {i}" });
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateAfterStartAsync("concurrent one");
        var second = CreateAfterStartAsync("concurrent two");
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Count(result => result is null).Should().Be(1, "only one request can consume the final slot");
        results.OfType<IntegrationApiKeyLimitException>().Should().ContainSingle();

        await using var verify = _db.CreateContext();
        (await verify.IntegrationApiKeys.CountAsync(key =>
            key.PlayerId == playerId && key.RevokedAtUtc == null &&
            (key.ExpiresAtUtc == null || key.ExpiresAtUtc > DateTime.UtcNow)))
            .Should().Be(IIntegrationApiKeyService.MaxActiveKeysPerPlayer);

        async Task<IntegrationApiKeyLimitException?> CreateAfterStartAsync(string name)
        {
            await start.Task;
            await using var context = _db.CreateContext();
            try
            {
                await Service(context).CreateAsync(playerId, new CreateIntegrationApiKeyDto { Name = name });
                return null;
            }
            catch (IntegrationApiKeyLimitException exception)
            {
                return exception;
            }
        }
    }

    private static IntegrationApiKeyService Service(TeamBuilderDbContext context) =>
        new(context, TimeProvider.System, NullLogger<IntegrationApiKeyService>.Instance);
}
