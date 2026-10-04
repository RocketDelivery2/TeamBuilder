using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamBuilder.Application.Models;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Application;

public sealed class PlayerOnboardingServiceTests : IDisposable
{
    private readonly TeamBuilderDbContext _context;

    public PlayerOnboardingServiceTests()
    {
        var options = new DbContextOptionsBuilder<TeamBuilderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TeamBuilderDbContext(options);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task GetByExternalIdentity_UsesExactIssuerAndSubject_WithoutTrackingOrMutation()
    {
        const string issuer = "https://accounts.example.com";
        const string subject = "provider|opaque-subject";
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = $"player-{Guid.NewGuid():N}",
            DisplayName = "Linked player"
        };
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
        _context.ChangeTracker.Clear();

        var service = new PlayerOnboardingService(_context);
        var result = await service.GetByExternalIdentityAsync(
            new ExternalIdentity(issuer, subject, "oidc", null));
        var issuerCaseMismatch = await service.GetByExternalIdentityAsync(
            new ExternalIdentity("https://accounts.Example.com", subject, "oidc", null));
        var subjectCaseMismatch = await service.GetByExternalIdentityAsync(
            new ExternalIdentity(issuer, "provider|OPAQUE-subject", "oidc", null));

        result.Should().NotBeNull();
        result!.Id.Should().Be(player.Id);
        issuerCaseMismatch.Should().BeNull();
        subjectCaseMismatch.Should().BeNull();
        _context.ChangeTracker.Entries().Should().BeEmpty("the lookup must remain AsNoTracking");
        _context.ChangeTracker.HasChanges().Should().BeFalse();
        (await _context.Players.CountAsync()).Should().Be(1);
        (await _context.PlayerIdentities.CountAsync()).Should().Be(1);
    }
}
