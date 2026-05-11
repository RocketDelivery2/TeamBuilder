using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// Sends a POST request with a per-request X-User-Id header so tests do not
    /// mutate shared HttpClient.DefaultRequestHeaders.
    /// </summary>
    private Task<HttpResponseMessage> PostWithUserIdAsync<T>(string url, T dto, Guid? userId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(dto)
        };
        request.Headers.Add("X-User-Id", (userId ?? Guid.NewGuid()).ToString());
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostJsonStringWithUserIdAsync(string url, string json, Guid? userId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-User-Id", (userId ?? Guid.NewGuid()).ToString());
        return _client.SendAsync(request);
    }

    // ── POST /api/v1/players ─────────────────────────────────────────────────

    [Fact]
    public async Task CreatePlayer_WithEmptyUsername_Returns400()
    {
        // Arrange — Username is [Required][StringLength(100, MinimumLength = 1)]
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
    public async Task CreateTeam_WithEmptyName_Returns400()
    {
        // Arrange — Name is [Required][StringLength(200, MinimumLength = 1)]
        var dto = new CreateTeamDto { Name = "" };

        // Act
        var response = await PostWithUserIdAsync("/api/v1/teams", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTeam_WithNameTooLong_Returns400()
    {
        // Arrange — Name [StringLength(200)]
        var dto = new CreateTeamDto { Name = new string('x', 201) };

        // Act
        var response = await PostWithUserIdAsync("/api/v1/teams", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTeam_WithMaxMembersOutOfRange_Returns400()
    {
        // Arrange — MaxMembers [Range(1, 1000)]
        var dto = new CreateTeamDto { Name = "Valid Name", MaxMembers = 0 };

        // Act
        var response = await PostWithUserIdAsync("/api/v1/teams", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── POST /api/v1/joinrequests ────────────────────────────────────────────

    [Fact]
    public async Task CreateJoinRequest_WithEmptyGuidTeamId_Returns400()
    {
        // Arrange — TeamId [Required][NonEmptyGuid]; Guid.Empty must be rejected
        var json = JsonSerializer.Serialize(new { TeamId = Guid.Empty });

        // Act
        var response = await PostJsonStringWithUserIdAsync("/api/v1/joinrequests", json);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateJoinRequest_WithMissingTeamId_Returns400()
    {
        // Arrange — TeamId [Required]; omitting the field should fail
        var json = "{}";

        // Act
        var response = await PostJsonStringWithUserIdAsync("/api/v1/joinrequests", json);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateJoinRequest_WithMessageTooLong_Returns400()
    {
        // Arrange — Message [StringLength(1000)]
        var dto = new CreateJoinRequestDto
        {
            TeamId = Guid.NewGuid(),
            Message = new string('m', 1001)
        };

        // Act
        var response = await PostJsonStringWithUserIdAsync(
            "/api/v1/joinrequests",
            JsonSerializer.Serialize(dto));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ProcessJoinRequest_WithInvalidEnumValue_Returns400()
    {
        // Arrange — Status [EnumDataType(typeof(RequestStatus))]; 99 is not a valid value
        var json = """{"status": 99}""";
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{Guid.NewGuid()}/process")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-User-Id", Guid.NewGuid().ToString());

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── POST /api/v1/events ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateEvent_WithEmptyName_Returns400()
    {
        // Arrange — Name is [Required][StringLength(200, MinimumLength = 1)]
        var dto = new CreateEventDto
        {
            Name = "",
            EventDateUtc = DateTime.UtcNow.AddDays(1)
        };

        // Act
        var response = await PostWithUserIdAsync("/api/v1/events", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEvent_WithMissingEventDate_Returns400()
    {
        // Arrange — EventDateUtc is DateTime? [Required]; omitting it sends null
        var json = """{"name": "Test Event", "maxParticipants": 10}""";

        // Act
        var response = await PostJsonStringWithUserIdAsync("/api/v1/events", json);

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

        // Act
        var response = await PostWithUserIdAsync("/api/v1/events", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── POST /api/v1/rosterimports ───────────────────────────────────────────

    [Fact]
    public async Task CreateRosterImport_WithEmptySourceName_Returns400()
    {
        // Arrange — SourceName is [Required][StringLength(200, MinimumLength = 1)]
        var dto = new CreateRosterImportDto
        {
            SourceName = "",
            SourceType = "csv",
            RawData = "data"
        };

        // Act
        var response = await PostWithUserIdAsync("/api/v1/rosterimports", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateRosterImport_WithEmptyRawData_Returns400()
    {
        // Arrange — RawData is [Required][MinLength(1)]
        var dto = new CreateRosterImportDto
        {
            SourceName = "Import",
            SourceType = "csv",
            RawData = ""
        };

        // Act
        var response = await PostWithUserIdAsync("/api/v1/rosterimports", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
