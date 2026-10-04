using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// HTTP-level behavior of <c>GET/POST /api/v1/players/me</c>: authentication, lookup by
/// Issuer + Subject, and onboarding. Database race behavior is covered against real SQL Server
/// in <c>PlayerOnboardingSqlServerIntegrationTests</c>.
/// </summary>
public sealed class PlayersMeIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private const string MeUrl = "/api/v1/players/me";
    private const string OtherIssuer = "https://accounts.example.com";

    private readonly TeamBuilderWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PlayersMeIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Signs a test token with an arbitrary issuer and claim set (no legacy GUID requirement).</summary>
    private static string CreateToken(IDictionary<string, object> claims, string issuer = TeamBuilderWebApplicationFactory.TestIssuer)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TeamBuilderWebApplicationFactory.TestSigningKey));
        var payload = new Dictionary<string, object>(claims)
        {
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString()
        };

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = TeamBuilderWebApplicationFactory.TestAudience,
            Claims = payload,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });
    }

    private static string SubjectToken(string subject, string issuer = TeamBuilderWebApplicationFactory.TestIssuer)
        => CreateToken(new Dictionary<string, object> { [JwtRegisteredClaimNames.Sub] = subject }, issuer);

    private static HttpRequestMessage Get(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, MeUrl);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Post(string? token, CreatePlayerDto dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MeUrl) { Content = JsonContent.Create(dto) };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static CreatePlayerDto NewPlayerDto(string? username = null) => new()
    {
        Username = username ?? $"me-{Guid.NewGuid():N}",
        DisplayName = "Onboarded Player",
        Region = "NA"
    };

    private static string NewSubject() => $"ext-{Guid.NewGuid():N}";

    private static async Task<List<PlayerIdentity>> GetIdentitiesAsync(WebApplicationFactory<Program> factory, string subject)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        return await db.PlayerIdentities.AsNoTracking().Where(pi => pi.Subject == subject).ToListAsync();
    }

    /// <summary>
    /// A factory variant with extra configuration. Clearing Jwt:Issuer disables issuer validation,
    /// which lets one test host accept tokens from several issuers.
    /// </summary>
    private WebApplicationFactory<Program> WithConfig(Dictionary<string, string?> settings)
        => _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

    // ── 401 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMe_Unauthenticated_Returns401()
    {
        using var response = await _client.SendAsync(Get(null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostMe_Unauthenticated_Returns401_AndCreatesNothing()
    {
        var dto = NewPlayerDto();

        using var response = await _client.SendAsync(Post(null, dto));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        (await db.Players.AnyAsync(p => p.Username == dto.Username)).Should().BeFalse();
    }

    [Fact]
    public async Task GetMe_TokenWithoutSubjectClaim_Returns401()
    {
        var token = CreateToken(new Dictionary<string, object> { ["name"] = "no subject" });

        using var response = await _client.SendAsync(Get(token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMe_TokenWithBadSignature_Returns401()
    {
        var token = SubjectToken(NewSubject());
        var tampered = token[..^4] + (token.EndsWith("AAAA") ? "BBBB" : "AAAA");

        using var response = await _client.SendAsync(Get(tampered));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMe_AuthenticatedButUnlinked_Returns404()
    {
        using var response = await _client.SendAsync(Get(SubjectToken(NewSubject())));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMe_LinkedIdentity_ReturnsLinkedPlayer()
    {
        var subject = NewSubject();
        var player = new Player
        {
            Id = Guid.NewGuid(),
            Username = $"linked-{Guid.NewGuid():N}",
            DisplayName = "Linked",
            Email = "linked@example.com"
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            db.Players.Add(player);
            db.PlayerIdentities.Add(new PlayerIdentity
            {
                Id = Guid.NewGuid(),
                PlayerId = player.Id,
                Issuer = TeamBuilderWebApplicationFactory.TestIssuer,
                Subject = subject,
                Provider = "oidc"
            });
            await db.SaveChangesAsync();
        }

        using var response = await _client.SendAsync(Get(SubjectToken(subject)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PlayerDto>();
        dto!.Id.Should().Be(player.Id);
        dto.Username.Should().Be(player.Username);
        dto.DisplayName.Should().Be("Linked");
        dto.Email.Should().Be("linked@example.com");
    }

    [Fact]
    public async Task GetMe_OrdinalSubjectMismatch_Returns404()
    {
        var subject = NewSubject();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            var player = new Player
            {
                Id = Guid.NewGuid(),
                Username = $"linked-{Guid.NewGuid():N}"
            };
            db.Players.Add(player);
            db.PlayerIdentities.Add(new PlayerIdentity
            {
                Id = Guid.NewGuid(),
                PlayerId = player.Id,
                Issuer = TeamBuilderWebApplicationFactory.TestIssuer,
                Subject = subject,
                Provider = "oidc"
            });
            await db.SaveChangesAsync();
        }

        using var response = await _client.SendAsync(Get(SubjectToken(subject + " ")));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostMe_NewIdentity_Returns201_WithGeneratedPlayerId_AndLinksIdentity()
    {
        // A GUID-shaped subject makes "Player.Id was not derived from Subject" observable.
        var subject = Guid.NewGuid().ToString();
        var dto = NewPlayerDto();
        dto.Email = "onboarded@example.com";

        using var response = await _client.SendAsync(Post(SubjectToken(subject), dto));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.AbsolutePath.Should().BeEquivalentTo(MeUrl);
        var created = await response.Content.ReadFromJsonAsync<PlayerDto>();
        created!.Username.Should().Be(dto.Username);
        created.DisplayName.Should().Be(dto.DisplayName);
        created.Region.Should().Be(dto.Region);
        created.Email.Should().Be(dto.Email);
        created.Id.Should().NotBe(Guid.Empty);
        created.Id.Should().NotBe(Guid.Parse(subject));

        var identity = (await GetIdentitiesAsync(_factory, subject)).Should().ContainSingle().Subject;
        identity.PlayerId.Should().Be(created.Id);
        identity.Issuer.Should().Be(TeamBuilderWebApplicationFactory.TestIssuer);
        identity.Subject.Should().Be(subject);

        using var me = await _client.SendAsync(Get(SubjectToken(subject)));
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await me.Content.ReadFromJsonAsync<PlayerDto>();
        profile!.Id.Should().Be(created.Id);
        profile.Email.Should().Be(dto.Email);
    }

    [Fact]
    public async Task PostMe_NonGuidSubject_IsAccepted()
    {
        var subject = "auth0|5f7c8ec7c33c6c004bbafe82";

        using var response = await _client.SendAsync(Post(SubjectToken(subject), NewPlayerDto()));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await GetIdentitiesAsync(_factory, subject)).Should().ContainSingle()
            .Which.Subject.Should().Be(subject);
    }

    [Fact]
    public async Task PostMe_SameIdentityTwice_Returns409_AndKeepsOriginalLink()
    {
        var token = SubjectToken(NewSubject());
        using var first = await _client.SendAsync(Post(token, NewPlayerDto()));
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var original = await first.Content.ReadFromJsonAsync<PlayerDto>();

        var secondDto = NewPlayerDto();
        using var second = await _client.SendAsync(Post(token, secondDto));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await second.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be(PlayerOnboardingService.IdentityAlreadyLinkedMessage);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
        (await db.Players.AnyAsync(p => p.Username == secondDto.Username)).Should().BeFalse();
        using var me = await _client.SendAsync(Get(token));
        (await me.Content.ReadFromJsonAsync<PlayerDto>())!.Id.Should().Be(original!.Id);
    }

    [Fact]
    public async Task PostMe_UsernameTaken_Returns409_WithExistingUsernameMessage_AndCreatesNoIdentity()
    {
        var username = $"taken-{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>();
            db.Players.Add(new Player { Id = Guid.NewGuid(), Username = username });
            await db.SaveChangesAsync();
        }
        var subject = NewSubject();

        using var response = await _client.SendAsync(Post(SubjectToken(subject), NewPlayerDto(username)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be($"Player with username '{username}' already exists.");
        (await GetIdentitiesAsync(_factory, subject)).Should().BeEmpty();
    }

    [Fact]
    public async Task PostMe_InvalidBody_Returns400()
    {
        using var response = await _client.SendAsync(Post(SubjectToken(NewSubject()), new CreatePlayerDto { Username = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostMe_SameSubjectUnderDifferentIssuers_OnboardsIndependently()
    {
        var factory = WithConfig(new() { ["Jwt:Issuer"] = "" });
        var client = factory.CreateClient();
        var subject = NewSubject();

        using var first = await client.SendAsync(Post(SubjectToken(subject, TeamBuilderWebApplicationFactory.TestIssuer), NewPlayerDto()));
        using var second = await client.SendAsync(Post(SubjectToken(subject, OtherIssuer), NewPlayerDto()));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstPlayer = await first.Content.ReadFromJsonAsync<PlayerDto>();
        var secondPlayer = await second.Content.ReadFromJsonAsync<PlayerDto>();
        firstPlayer!.Id.Should().NotBe(secondPlayer!.Id);

        var identities = await GetIdentitiesAsync(factory, subject);
        identities.Select(i => (i.Issuer, i.PlayerId)).Should().BeEquivalentTo(new[]
        {
            (TeamBuilderWebApplicationFactory.TestIssuer, firstPlayer.Id),
            (OtherIssuer, secondPlayer.Id)
        });
    }

    // ── configuration: Entra-style oid subject + metadata ────────────────────

    [Fact]
    public async Task PostMe_WithEntraConfiguration_UsesOidAsSubject_AndStoresProviderAndTenant()
    {
        var factory = WithConfig(new()
        {
            ["Jwt:ExternalIdentity:SubjectClaim"] = "oid",
            ["Jwt:ExternalIdentity:TenantIdClaim"] = "tid",
            ["Jwt:ExternalIdentity:Provider"] = "entra"
        });
        var client = factory.CreateClient();
        var oid = Guid.NewGuid().ToString();
        var tid = Guid.NewGuid().ToString();
        // Entra's pairwise "sub" is not a GUID and must not be used as the identity key.
        var token = CreateToken(new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = "AAAAAAAAAAAAAAAAAAAAAIkzqFVrSaSaFHy782bbtaQ",
            ["oid"] = oid,
            ["tid"] = tid
        });

        using var response = await client.SendAsync(Post(token, NewPlayerDto()));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<PlayerDto>();
        created!.Id.Should().NotBe(Guid.Parse(oid));
        var identity = (await GetIdentitiesAsync(factory, oid)).Should().ContainSingle().Subject;
        identity.PlayerId.Should().Be(created.Id);
        identity.Issuer.Should().Be(TeamBuilderWebApplicationFactory.TestIssuer);
        identity.Provider.Should().Be("entra");
        identity.TenantId.Should().Be(tid);

        using var me = await client.SendAsync(Get(token));
        me.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostMe_WithDefaultConfiguration_StoresDefaultProvider_AndNullTenantWhenAbsent()
    {
        var subject = NewSubject();

        using var response = await _client.SendAsync(Post(SubjectToken(subject), NewPlayerDto()));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var identity = (await GetIdentitiesAsync(_factory, subject)).Should().ContainSingle().Subject;
        identity.Provider.Should().Be("oidc");
        identity.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task GetMe_WithEntraConfiguration_TokenMissingOid_Returns401()
    {
        var client = WithConfig(new() { ["Jwt:ExternalIdentity:SubjectClaim"] = "oid" }).CreateClient();

        using var response = await client.SendAsync(Get(SubjectToken(NewSubject())));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── migrated team endpoints accept the same identity ─────────────────────

    [Fact]
    public async Task NonGuidSubjectToken_IsAuthenticatedOnTeamsButForbiddenUntilLinked()
    {
        // POST /api/v1/teams now uses the ExternalIdentity scheme: a non-GUID subject authenticates
        // (no 401), and an identity with no linked player is forbidden.
        var token = SubjectToken(NewSubject());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/teams")
        {
            Content = JsonContent.Create(new CreateTeamDto { Name = $"Team-{Guid.NewGuid():N}", MaxMembers = 5 })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
