using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using TeamBuilder.Application.DTOs;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Verifies that invalid request payloads are rejected with 400 Bad Request
/// due to model validation attributes on the request DTOs.
/// </summary>
public sealed class ValidationIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ValidationIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ── POST /api/v1/players ─────────────────────────────────────────────────

    [Fact]
    public async Task CreatePlayer_WithMissingUsername_Returns400()
    {
        // Arrange — Username is [Required]
        var dto = new CreatePlayerDto { Username = "" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/players", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreatePlayer_WithUsernameTooLong_Returns400()
    {
        // Arrange — Username [StringLength(100)]
        var dto = new CreatePlayerDto { Username = new string('a', 101) };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/players", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreatePlayer_WithInvalidEmail_Returns400()
    {
        // Arrange — Email [EmailAddress]
        var dto = new CreatePlayerDto
        {
            Username = $"val-{Guid.NewGuid():N}",
            Email = "not-an-email"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/players", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── POST /api/v1/teams ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateTeam_WithMissingName_Returns400()
    {
        // Arrange — Name is [Required]
        var dto = new CreateTeamDto { Name = "" };
        _client.DefaultRequestHeaders.Remove("X-User-Id");
        _client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/teams", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTeam_WithNameTooLong_Returns400()
    {
        // Arrange — Name [StringLength(200)]
        var dto = new CreateTeamDto { Name = new string('x', 201) };
        _client.DefaultRequestHeaders.Remove("X-User-Id");
        _client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/teams", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTeam_WithMaxMembersOutOfRange_Returns400()
    {
        // Arrange — MaxMembers [Range(1, 1000)]
        var dto = new CreateTeamDto { Name = "Valid Name", MaxMembers = 0 };
        _client.DefaultRequestHeaders.Remove("X-User-Id");
        _client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/teams", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── POST /api/v1/events ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateEvent_WithMissingName_Returns400()
    {
        // Arrange — Name is [Required]
        var dto = new CreateEventDto
        {
            Name = "",
            EventDateUtc = DateTime.UtcNow.AddDays(1)
        };
        _client.DefaultRequestHeaders.Remove("X-User-Id");
        _client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/events", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEvent_WithMaxParticipantsOutOfRange_Returns400()
    {
        // Arrange — MaxParticipants [Range(1, 100000)]
        var dto = new CreateEventDto
        {
            Name = "Valid Event",
            EventDateUtc = DateTime.UtcNow.AddDays(1),
            MaxParticipants = 0
        };
        _client.DefaultRequestHeaders.Remove("X-User-Id");
        _client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/events", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── POST /api/v1/rosterimports ───────────────────────────────────────────

    [Fact]
    public async Task CreateRosterImport_WithMissingSourceName_Returns400()
    {
        // Arrange — SourceName is [Required]
        var dto = new CreateRosterImportDto
        {
            SourceName = "",
            SourceType = "csv",
            RawData = "data"
        };
        _client.DefaultRequestHeaders.Remove("X-User-Id");
        _client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/rosterimports", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateRosterImport_WithMissingRawData_Returns400()
    {
        // Arrange — RawData is [Required][MinLength(1)]
        var dto = new CreateRosterImportDto
        {
            SourceName = "Import",
            SourceType = "csv",
            RawData = ""
        };
        _client.DefaultRequestHeaders.Remove("X-User-Id");
        _client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/rosterimports", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
