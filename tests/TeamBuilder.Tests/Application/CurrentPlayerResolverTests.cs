using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;
using TeamBuilder.Tests.Integration;

namespace TeamBuilder.Tests.Application;

/// <summary>
/// Resolver behavior: external identity (exact Issuer + Subject) to internal Player.Id.
/// The SQL Server binary-collation behavior for the same lookups is covered in
/// <see cref="CurrentPlayerResolverSqlServerIntegrationTests"/>.
/// </summary>
public class CurrentPlayerResolverTests : IDisposable
{
    private const string Issuer = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0";
    private const string OtherIssuer = "https://accounts.example.com";

    private readonly TeamBuilderDbContext _context;

    public CurrentPlayerResolverTests()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TeamBuilderDbContext(options);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task LinkedIdentity_ResolvesLinkedPlayerId()
    {
        var subject = Guid.NewGuid().ToString();
        var playerId = await SeedLinkedPlayerAsync(Issuer, subject);
        await SeedLinkedPlayerAsync(Issuer, Guid.NewGuid().ToString());

        var resolved = await Resolve(new ExternalIdentity(Issuer, subject, "entra", null));

        resolved.Should().Be(playerId);
    }

    [Fact]
    public async Task LinkedIdentity_ResolvesPlayerId_NotDerivedFromSubject()
    {
        var subject = Guid.NewGuid().ToString();
        var playerId = await SeedLinkedPlayerAsync(Issuer, subject);

        var resolved = await Resolve(new ExternalIdentity(Issuer, subject, "entra", null));

        resolved.Should().Be(playerId).And.NotBe(Guid.Parse(subject));
    }

    [Fact]
    public async Task UnlinkedAuthenticatedIdentity_ResolvesNull()
    {
        await SeedLinkedPlayerAsync(Issuer, "someone-else");

        var resolved = await Resolve(new ExternalIdentity(Issuer, "not-linked", "entra", null));

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task MissingExternalIdentity_ResolvesNull()
    {
        await SeedLinkedPlayerAsync(Issuer, "someone");

        var resolved = await Resolve(null);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task NonGuidSubject_Resolves()
    {
        const string subject = "auth0|5f7c8ec7c33c6c004bbafe82";
        var playerId = await SeedLinkedPlayerAsync(OtherIssuer, subject);

        var resolved = await Resolve(new ExternalIdentity(OtherIssuer, subject, "oidc", null));

        resolved.Should().Be(playerId);
    }

    [Fact]
    public async Task SameSubjectUnderDifferentIssuers_ResolvesIndependently()
    {
        const string subject = "shared-subject";
        var firstPlayerId = await SeedLinkedPlayerAsync(Issuer, subject);
        var secondPlayerId = await SeedLinkedPlayerAsync(OtherIssuer, subject);

        (await Resolve(new ExternalIdentity(Issuer, subject, "entra", null))).Should().Be(firstPlayerId);
        (await Resolve(new ExternalIdentity(OtherIssuer, subject, "oidc", null))).Should().Be(secondPlayerId);
        (await Resolve(new ExternalIdentity("https://third.example.com", subject, "oidc", null))).Should().BeNull();
    }

    [Fact]
    public async Task ProviderAndTenant_AreNotPartOfTheLookupKey()
    {
        var playerId = await SeedLinkedPlayerAsync(Issuer, "subject-1");

        var resolved = await Resolve(new ExternalIdentity(Issuer, "subject-1", "different-provider", "different-tenant"));

        resolved.Should().Be(playerId);
    }

    [Theory]
    [InlineData("https://ACCOUNTS.example.com")]
    [InlineData("HTTPS://accounts.example.com")]
    [InlineData("https://accounts.example.com/")]
    [InlineData(" https://accounts.example.com")]
    [InlineData("https://accounts.example.com/.")]
    [InlineData("https://accounts.example.com ")]
    public async Task IssuerVariants_AreNotNormalized(string issuerVariant)
    {
        await SeedLinkedPlayerAsync(OtherIssuer, "subject-1");

        var resolved = await Resolve(new ExternalIdentity(issuerVariant, "subject-1", "oidc", null));

        resolved.Should().BeNull("Issuer is an exact key; '{0}' is a different issuer than '{1}'", issuerVariant, OtherIssuer);
    }

    [Fact]
    public async Task IssuersDifferingOnlyByTrailingSlash_ResolveToTheirOwnPlayers()
    {
        var withoutSlash = await SeedLinkedPlayerAsync("https://accounts.example.com", "subject-1");
        var withSlash = await SeedLinkedPlayerAsync("https://accounts.example.com/", "subject-1");

        (await Resolve(new ExternalIdentity("https://accounts.example.com", "subject-1", "oidc", null))).Should().Be(withoutSlash);
        (await Resolve(new ExternalIdentity("https://accounts.example.com/", "subject-1", "oidc", null))).Should().Be(withSlash);
    }

    [Fact]
    public async Task SubjectCaseDifferences_AreNotNormalized()
    {
        await SeedLinkedPlayerAsync(OtherIssuer, "AbC123");

        var resolved = await Resolve(new ExternalIdentity(OtherIssuer, "abc123", "oidc", null));

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_DoesNotModifyOrTrackDatabaseState()
    {
        var playerId = await SeedLinkedPlayerAsync(Issuer, "subject-1");
        var before = await SnapshotAsync();
        _context.ChangeTracker.Clear();

        await Resolve(new ExternalIdentity(Issuer, "subject-1", "entra", null));
        await Resolve(new ExternalIdentity(Issuer, "unlinked", "entra", null));
        await Resolve(null);

        _context.ChangeTracker.Entries().Should().BeEmpty("the lookup must be AsNoTracking");
        _context.ChangeTracker.HasChanges().Should().BeFalse();
        (await SnapshotAsync()).Should().BeEquivalentTo(before);
        playerId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Resolver_IsRegisteredInApiContainer()
    {
        await using var factory = new TeamBuilderWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<ICurrentPlayerResolver>();

        resolver.Should().BeOfType<CurrentPlayerResolver>();
        (await resolver.ResolvePlayerIdAsync()).Should().BeNull("there is no authenticated caller outside a request");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<Guid?> Resolve(ExternalIdentity? identity)
        => new CurrentPlayerResolver(new FixedExternalIdentityAccessor(identity), _context).ResolvePlayerIdAsync();

    private async Task<Guid> SeedLinkedPlayerAsync(string issuer, string subject)
    {
        var player = new Player { Id = Guid.NewGuid(), Username = $"player_{Guid.NewGuid():N}" };
        _context.Players.Add(player);
        _context.PlayerIdentities.Add(new PlayerIdentity
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Issuer = issuer,
            Subject = subject,
            Provider = "oidc"
        });
        await _context.SaveChangesAsync();
        return player.Id;
    }

    private async Task<object> SnapshotAsync()
    {
        var players = await _context.Players.AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Username, p.CreatedAtUtc, p.UpdatedAtUtc })
            .ToListAsync();
        var identities = await _context.PlayerIdentities.AsNoTracking()
            .OrderBy(pi => pi.Id)
            .Select(pi => new { pi.Id, pi.PlayerId, pi.Issuer, pi.Subject, pi.Provider, pi.TenantId, pi.CreatedAtUtc, pi.UpdatedAtUtc })
            .ToListAsync();
        return new { players, identities };
    }

    private sealed class FixedExternalIdentityAccessor(ExternalIdentity? identity) : IExternalIdentityAccessor
    {
        public ExternalIdentity? Current { get; } = identity;
    }
}
