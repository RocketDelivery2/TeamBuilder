using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;
using TeamBuilder.Tests.Integration;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Proves onboarding against a real SQL Server: Player + PlayerIdentity are written atomically,
/// and losing a race on the Issuer + Subject or Username unique index becomes a deterministic
/// conflict (InvalidOperationException, which the API maps to 409) instead of a 500.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class PlayerOnboardingSqlServerIntegrationTests : IAsyncLifetime
{
    private const string EntraIssuer = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0";
    private const string OtherIssuer = "https://accounts.example.com";
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private readonly SqlServerContainerFixture _fixture;
    private SqlServerTestDatabase _db = null!;

    public PlayerOnboardingSqlServerIntegrationTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = new SqlServerTestDatabase(_fixture, "onboarding");
        await _db.MigrateToAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ── persistence ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Onboard_PersistsPlayerWithGeneratedId_AndIdentityWithAllFields()
    {
        var oid = Guid.NewGuid().ToString();
        var identity = new ExternalIdentity(EntraIssuer, oid, "entra", TenantId);

        PlayerDto created;
        await using (var context = _db.CreateContext())
        {
            created = await new PlayerOnboardingService(context).OnboardAsync(identity, NewDto());
        }

        created.Id.Should().NotBe(Guid.Empty);
        created.Id.Should().NotBe(Guid.Parse(oid));

        await using var verify = _db.CreateContext();
        var player = await verify.Players.SingleAsync(p => p.Id == created.Id);
        player.Username.Should().Be(created.Username);
        var link = await verify.PlayerIdentities.SingleAsync(pi => pi.PlayerId == created.Id);
        link.Issuer.Should().Be(EntraIssuer);
        link.Subject.Should().Be(oid);
        link.Provider.Should().Be("entra");
        link.TenantId.Should().Be(TenantId);

        var found = await new PlayerOnboardingService(verify).GetByExternalIdentityAsync(identity);
        found!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task Onboard_NullTenant_PersistsNullTenant()
    {
        var identity = new ExternalIdentity(OtherIssuer, "user-without-tenant", "oidc", null);

        await using (var context = _db.CreateContext())
            await new PlayerOnboardingService(context).OnboardAsync(identity, NewDto());

        await using var verify = _db.CreateContext();
        var link = await verify.PlayerIdentities.SingleAsync(pi => pi.Subject == identity.Subject);
        link.Provider.Should().Be("oidc");
        link.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task GetByExternalIdentity_MatchesIssuerAndSubjectExactly()
    {
        var identity = new ExternalIdentity(OtherIssuer, "AbC123", "oidc", null);
        await using (var context = _db.CreateContext())
            await new PlayerOnboardingService(context).OnboardAsync(identity, NewDto());

        await using var verify = _db.CreateContext();
        var service = new PlayerOnboardingService(verify);
        (await service.GetByExternalIdentityAsync(identity)).Should().NotBeNull();
        (await service.GetByExternalIdentityAsync(identity with { Subject = "abc123" })).Should().BeNull();
        (await service.GetByExternalIdentityAsync(identity with { Issuer = EntraIssuer })).Should().BeNull();
    }

    [Fact]
    public async Task Onboard_SameSubjectUnderDifferentIssuers_CreatesTwoPlayers()
    {
        var subject = Guid.NewGuid().ToString();

        await using var context = _db.CreateContext();
        var service = new PlayerOnboardingService(context);
        var first = await service.OnboardAsync(new ExternalIdentity(EntraIssuer, subject, "entra", TenantId), NewDto());
        var second = await service.OnboardAsync(new ExternalIdentity(OtherIssuer, subject, "oidc", null), NewDto());

        first.Id.Should().NotBe(second.Id);
        await using var verify = _db.CreateContext();
        (await verify.PlayerIdentities.CountAsync(pi => pi.Subject == subject)).Should().Be(2);
    }

    [Fact]
    public async Task Onboard_AlreadyLinkedIdentity_ThrowsConflict()
    {
        var identity = new ExternalIdentity(EntraIssuer, Guid.NewGuid().ToString(), "entra", TenantId);
        await using (var context = _db.CreateContext())
            await new PlayerOnboardingService(context).OnboardAsync(identity, NewDto());

        var secondDto = NewDto();
        await using (var context = _db.CreateContext())
        {
            var act = () => new PlayerOnboardingService(context).OnboardAsync(identity, secondDto);
            await act.Should().ThrowExactlyAsync<InvalidOperationException>()
                .WithMessage(PlayerOnboardingService.IdentityAlreadyLinkedMessage);
        }

        await using var verify = _db.CreateContext();
        (await verify.Players.AnyAsync(p => p.Username == secondDto.Username)).Should().BeFalse();
    }

    // ── races (the unique indexes are authoritative) ─────────────────────────

    [Fact]
    public async Task Onboard_LosingIdentityRace_ThrowsConflict_AndLeavesNoOrphanPlayer()
    {
        var identity = new ExternalIdentity(EntraIssuer, Guid.NewGuid().ToString(), "entra", TenantId);
        var winnerId = await SeedPlayerAsync();
        var dto = NewDto();

        // Another request links the identity after our pre-check but before our insert.
        await using (var context = CreateContextWithBeforeSave(() => InsertIdentityAsync(winnerId, identity)))
        {
            var act = () => new PlayerOnboardingService(context).OnboardAsync(identity, dto);
            await act.Should().ThrowExactlyAsync<InvalidOperationException>()
                .WithMessage(PlayerOnboardingService.IdentityAlreadyLinkedMessage);
        }

        await using var verify = _db.CreateContext();
        (await verify.Players.AnyAsync(p => p.Username == dto.Username))
            .Should().BeFalse("the Player insert must roll back with the failed PlayerIdentity insert");
        (await verify.PlayerIdentities.SingleAsync(pi => pi.Subject == identity.Subject))
            .PlayerId.Should().Be(winnerId);
    }

    [Fact]
    public async Task Onboard_LosingUsernameRace_ThrowsUsernameConflict_AndCreatesNoIdentity()
    {
        var identity = new ExternalIdentity(OtherIssuer, $"user-{Guid.NewGuid():N}", "oidc", null);
        var dto = NewDto();

        // Another request takes the username after our pre-check but before our insert.
        await using (var context = CreateContextWithBeforeSave(() => SeedPlayerAsync(dto.Username)))
        {
            var act = () => new PlayerOnboardingService(context).OnboardAsync(identity, dto);
            await act.Should().ThrowExactlyAsync<InvalidOperationException>()
                .WithMessage($"Player with username '{dto.Username}' already exists.");
        }

        await using var verify = _db.CreateContext();
        (await verify.Players.CountAsync(p => p.Username == dto.Username)).Should().Be(1);
        (await verify.PlayerIdentities.AnyAsync(pi => pi.Subject == identity.Subject)).Should().BeFalse();
    }

    [Fact]
    public async Task Onboard_ConcurrentSameIdentity_ExactlyOneSucceeds_OthersConflict()
    {
        const int attempts = 8;
        var identity = new ExternalIdentity(EntraIssuer, Guid.NewGuid().ToString(), "entra", TenantId);
        var dtos = Enumerable.Range(0, attempts).Select(_ => NewDto()).ToList();
        using var start = new SemaphoreSlim(0);

        var tasks = dtos.Select(async dto =>
        {
            await start.WaitAsync();
            await using var context = _db.CreateContext();
            try
            {
                await new PlayerOnboardingService(context).OnboardAsync(identity, dto);
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }).ToList();
        start.Release(attempts);
        var outcomes = await Task.WhenAll(tasks);

        outcomes.Count(o => o is null).Should().Be(1);
        outcomes.Where(o => o is not null).Should().AllSatisfy(o =>
        {
            o.Should().BeOfType<InvalidOperationException>();
            o!.Message.Should().Be(PlayerOnboardingService.IdentityAlreadyLinkedMessage);
        });

        await using var verify = _db.CreateContext();
        var usernames = dtos.Select(d => d.Username).ToList();
        (await verify.Players.CountAsync(p => usernames.Contains(p.Username))).Should().Be(1);
        (await verify.PlayerIdentities.CountAsync(pi => pi.Subject == identity.Subject)).Should().Be(1);
    }

    [Fact]
    public async Task PostMe_ConcurrentSameIdentity_OverHttp_Returns201Once_And409Otherwise()
    {
        const int attempts = 8;
        await using var factory = new SqlServerApiFactory(_db.ConnectionString);
        var client = factory.CreateClient();
        var subject = $"ext-{Guid.NewGuid():N}";
        var token = TeamBuilderWebApplicationFactory.CreateTestJwtWithSubject(subject);
        using var start = new SemaphoreSlim(0);

        var tasks = Enumerable.Range(0, attempts).Select(async _ =>
        {
            await start.WaitAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/players/me")
            {
                Content = JsonContent.Create(NewDto())
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }).ToList();
        start.Release(attempts);
        var statuses = await Task.WhenAll(tasks);

        statuses.Count(s => s == HttpStatusCode.Created).Should().Be(1);
        statuses.Count(s => s == HttpStatusCode.Conflict).Should().Be(attempts - 1);

        await using var verify = _db.CreateContext();
        (await verify.PlayerIdentities.CountAsync(pi => pi.Subject == subject)).Should().Be(1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static CreatePlayerDto NewDto() => new() { Username = $"onboard_{Guid.NewGuid():N}", DisplayName = "Onboarded" };

    private TeamBuilderDbContext CreateContextWithBeforeSave(Func<Task> beforeSave)
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseSqlServer(_db.ConnectionString)
            .AddInterceptors(new BeforeSaveInterceptor(beforeSave))
            .Options;
        return new TeamBuilderDbContext(options);
    }

    private async Task<Guid> SeedPlayerAsync(string? username = null)
    {
        await using var context = _db.CreateContext();
        var player = new Player { Id = Guid.NewGuid(), Username = username ?? $"player_{Guid.NewGuid():N}" };
        context.Players.Add(player);
        await context.SaveChangesAsync();
        return player.Id;
    }

    private async Task InsertIdentityAsync(Guid playerId, ExternalIdentity identity)
    {
        await using var context = _db.CreateContext();
        context.PlayerIdentities.Add(new PlayerIdentity
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Issuer = identity.Issuer,
            Subject = identity.Subject,
            Provider = identity.Provider,
            TenantId = identity.TenantId
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Runs a competing write once, right before the intercepted context saves.</summary>
    private sealed class BeforeSaveInterceptor(Func<Task> beforeSave) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await beforeSave();
            }

            return result;
        }
    }

    /// <summary>The real API pipeline backed by the test's SQL Server database.</summary>
    private sealed class SqlServerApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:TeamBuilderSql"] = connectionString,
                    ["Jwt:SigningKey"] = TeamBuilderWebApplicationFactory.TestSigningKey,
                    ["Jwt:Issuer"] = TeamBuilderWebApplicationFactory.TestIssuer,
                    ["Jwt:Audience"] = TeamBuilderWebApplicationFactory.TestAudience
                });
            });

            builder.ConfigureServices(services =>
            {
                var dbDescriptors = services
                    .Where(d =>
                        d.ServiceType == typeof(DbContextOptions<TeamBuilderDbContext>) ||
                        d.ServiceType == typeof(DbContextOptions) ||
                        d.ServiceType.FullName?.StartsWith("Microsoft.EntityFrameworkCore") == true &&
                        d.ServiceType.FullName.Contains("TeamBuilderDbContext"))
                    .ToList();
                foreach (var d in dbDescriptors)
                    services.Remove(d);

                services.AddDbContext<TeamBuilderDbContext>(options => options.UseSqlServer(connectionString));
            });

            builder.UseEnvironment("Development");
        }
    }
}
