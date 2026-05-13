using System.Net;
using System.Net.Http.Headers;
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
    /// Sends a POST request with a JWT Bearer token so tests pass the [Authorize] gate
    /// and reach the model validation layer.
    /// </summary>
    private Task<HttpResponseMessage> PostWithJwtAsync<T>(string url, T dto, Guid? userId = null)
    {
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(userId ?? Guid.NewGuid());
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(dto)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostJsonStringWithJwtAsync(string url, string json, Guid? userId = null)
    {
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(userId ?? Guid.NewGuid());
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    // -- POST /api/v1/players -------------------------------------------------

    [Fact]
    public async Task CreatePlayer_WithEmptyUsername_Returns400()
    {
        var dto = new CreatePlayerDto { Username = "" };
        var response = await _client.PostAsJsonAsync("/api/v1/players", dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreatePlayer_WithUsernameTooLong_Returns400()
    {
        var dto = new CreatePlayerDto { Username = new string('a', 101) };
        var response = await _client.PostAsJsonAsync("/api/v1/players", dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreatePlayer_WithInvalidEmail_Returns400()
    {
        var dto = new CreatePlayerDto
        {
            Username = $"val-{Guid.NewGuid():N}",
            Email = "not-an-email"
        };
        var response = await _client.PostAsJsonAsync("/api/v1/players", dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -- POST /api/v1/teams ---------------------------------------------------

    [Fact]
    public async Task CreateTeam_WithEmptyName_Returns400()
    {
        var dto = new CreateTeamDto { Name = "" };
        var response = await PostWithJwtAsync("/api/v1/teams", dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTeam_WithNameTooLong_Returns400()
    {
        var dto = new CreateTeamDto { Name = new string('x', 201) };
        var response = await PostWithJwtAsync("/api/v1/teams", dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTeam_WithMaxMembersOutOfRange_Returns400()
    {
        var dto = new CreateTeamDto { Name = "Valid Name", MaxMembers = 0 };
        var response = await PostWithJwtAsync("/api/v1/teams", dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -- POST /api/v1/joinrequests --------------------------------------------

    [Fact]
    public async Task CreateJoinRequest_WithEmptyGuidTeamId_Returns400()
    {
        var json = JsonSerializer.Serialize(new { TeamId = Guid.Empty });
        var response = await PostJsonStringWithJwtAsync("/api/v1/joinrequests", json);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateJoinRequest_WithMissingTeamId_Returns400()
    {
        var response = await PostJsonStringWithJwtAsync("/api/v1/joinrequests", "{}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateJoinRequest_WithMessageTooLong_Returns400()
    {
        var dto = new CreateJoinRequestDto
        {
            TeamId = Guid.NewGuid(),
            Message = new string('m', 1001)
        };
        var response = await PostJsonStringWithJwtAsync("/api/v1/joinrequests", JsonSerializer.Serialize(dto));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ProcessJoinRequest_WithInvalidEnumValue_Returns400()
    {
        var json = """{"status": 99}""";
        var token = TeamBuilderWebApplicationFactory.CreateTestJwt(Guid.NewGuid());
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/joinrequests/{Guid.NewGuid()}/process")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -- POST /api/v1/events --------------------------------------------------

    [Fact]
    public async Task CreateEvent_WithEmptyName_Returns400()
    {
        var dto = new CreateEventDto { Name = "", EventDateUtc = DateTime.UtcNow.AddDays(1) };
        var response = await PostWithJwtAsync("/api/v1/events", dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEvent_WithMissingEventDate_Returns400()
    {
        var json = """{"name": "Test Event", "maxParticipants": 10}""";
        var response = await PostJsonStringWithJwtAsync("/api/v1/events", json);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEvent_WithMaxParticipantsOutOfRange_Returns400()
    {
        var dto = new CreateEventDto
        {
            Name = "Valid Event",
            EventDateUtc = DateTime.UtcNow.AddDays(1),
            MaxParticipants = 0
        };
        var response = await PostWithJwtAsync("/api/v1/events", dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -- POST /api/v1/rosterimports -------------------------------------------

    [Fact]
    public async Task CreateRosterImport_WithEmptySourceName_Returns400()
    {
        var dto = new CreateRosterImportDto { SourceName = "", SourceType = "csv", RawData = "data" };
        var response = await PostWithJwtAsync("/api/v1/rosterimports", dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateRosterImport_WithEmptyRawData_Returns400()
    {
        var dto = new CreateRosterImportDto { SourceName = "Import", SourceType = "csv", RawData = "" };
        var response = await PostWithJwtAsync("/api/v1/rosterimports", dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}