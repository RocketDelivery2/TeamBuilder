using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Domain.Enums;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Outbox;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// Shared HTTP and database helpers for the rapid-refill suites: a pickup game with one
/// requirement, linked players, roster calls, "notify me", notifications, and an
/// <see cref="OutboxProcessor"/> driven explicitly (the hosted worker is disabled in tests).
/// </summary>
internal sealed class RefillHarness(TeamBuilderWebApplicationFactory factory, HttpClient client, Func<TeamBuilderDbContext> createContext)
{
    public static string RosterUrl(Guid occurrenceId) => $"/api/v1/events/{occurrenceId}/roster";

    public static string SubscriptionUrl(Guid occurrenceId, Guid requirementId) =>
        $"{RosterUrl(occurrenceId)}/requirements/{requirementId}/subscription";

    public TeamBuilderDbContext CreateContext() => createContext();

    /// <summary>
    /// A processor over the API's own DI container, with its own clock (default: now + 1 s, so
    /// every message staged so far is due) and options.
    /// </summary>
    public OutboxProcessor Processor(TimeProvider? clock = null, Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions { BatchSize = 50, LeaseDuration = TimeSpan.FromMinutes(1), MaxAttempts = 5 };
        configure?.Invoke(options);
        return new OutboxProcessor(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            clock ?? new Application.FixedTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1)),
            Options.Create(options),
            NullLogger<OutboxProcessor>.Instance);
    }

    public async Task<(Guid HostId, string HostToken, Guid OccurrenceId, Guid RequirementId)> SeedGameAsync(
        int requiredCount = 10,
        string name = "Wednesday Basketball",
        string roleCode = "participant")
    {
        var hostId = Guid.NewGuid();
        var hostToken = await LinkedPlayerTokens.ForPlayerAsync(factory.Services, hostId);

        await using var context = createContext();
        var occurrence = new EventOccurrence
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = "basketball",
            ScheduledStartUtc = new DateTime(2026, 10, 15, 1, 0, 0, DateTimeKind.Utc),
            Status = EventStatus.Open,
            MaxParticipants = requiredCount,
            HostId = hostId
        };
        context.Events.Add(occurrence);
        await context.SaveChangesAsync();

        var requirementId = await CreateRequirementAsync(hostToken, occurrence.Id, roleCode, requiredCount);
        return (hostId, hostToken, occurrence.Id, requirementId);
    }

    public async Task<Guid> CreateRequirementAsync(string hostToken, Guid occurrenceId, string roleCode, int requiredCount)
    {
        var response = await SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/requirements", hostToken, new { roleCode, requiredCount });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RosterRequirementDto>())!.Id;
    }

    public async Task<(Guid PlayerId, string Token)> NewPlayerAsync()
    {
        var playerId = Guid.NewGuid();
        return (playerId, await LinkedPlayerTokens.ForPlayerAsync(factory.Services, playerId));
    }

    public async Task<List<(Guid PlayerId, string Token, Guid AssignmentId)>> FillAsync(Guid occurrenceId, Guid requirementId, int count)
    {
        var holders = new List<(Guid, string, Guid)>();
        for (var i = 0; i < count; i++)
        {
            var (playerId, token) = await NewPlayerAsync();
            var response = await ClaimAsync(token, occurrenceId, requirementId);
            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            holders.Add((playerId, token, (await response.Content.ReadFromJsonAsync<RosterAssignmentDto>())!.Id));
        }
        return holders;
    }

    public Task<HttpResponseMessage> ClaimAsync(string token, Guid occurrenceId, Guid requirementId) =>
        SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/claims", token, new { requirementId });

    public Task<HttpResponseMessage> LeaveAsync(string token, Guid occurrenceId, Guid assignmentId) =>
        SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/assignments/{assignmentId}/leave", token);

    public Task<HttpResponseMessage> HostActAsync(string hostToken, Guid occurrenceId, Guid assignmentId, string action) =>
        SendAsync(HttpMethod.Post, $"{RosterUrl(occurrenceId)}/assignments/{assignmentId}/{action}", hostToken);

    public Task<HttpResponseMessage> SubscribeAsync(string token, Guid occurrenceId, Guid requirementId) =>
        SendAsync(HttpMethod.Put, SubscriptionUrl(occurrenceId, requirementId), token);

    public Task<HttpResponseMessage> UnsubscribeAsync(string token, Guid occurrenceId, Guid requirementId) =>
        SendAsync(HttpMethod.Delete, SubscriptionUrl(occurrenceId, requirementId), token);

    public async Task<InAppNotificationPageDto> NotificationsAsync(string token, bool unreadOnly = false, int? pageSize = null, string? cursor = null)
    {
        var query = $"?unreadOnly={unreadOnly.ToString().ToLowerInvariant()}";
        if (pageSize is { } size) query += $"&pageSize={size}";
        if (cursor is not null) query += $"&cursor={Uri.EscapeDataString(cursor)}";
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/players/me/notifications{query}", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<InAppNotificationPageDto>())!;
    }

    public async Task<int> UnreadCountAsync(string token)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/players/me/notifications/unread-count", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UnreadNotificationCountDto>())!.UnreadCount;
    }

    public Task<HttpResponseMessage> MarkReadAsync(string token, Guid notificationId) =>
        SendAsync(HttpMethod.Post, $"/api/v1/players/me/notifications/{notificationId}/read", token);

    public async Task<OccurrenceDetailDto> DetailAsync(Guid occurrenceId, string? token = null)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/events/{occurrenceId}/detail", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<OccurrenceDetailDto>())!;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    /// <summary>Starts every operation, then releases them together through one gate. Never a 500.</summary>
    public static async Task<List<HttpResponseMessage>> RaceAsync(IEnumerable<Func<Task<HttpResponseMessage>>> operations)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = operations.Select(async operation =>
        {
            await gate.Task;
            return await operation();
        }).ToList();
        gate.SetResult();
        var responses = (await Task.WhenAll(tasks)).ToList();
        responses.Should().NotContain(r => r.StatusCode == HttpStatusCode.InternalServerError);
        return responses;
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(text))
            return null;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public async Task<List<OutboxMessage>> OutboxAsync(Guid occurrenceId)
    {
        await using var context = createContext();
        return await context.OutboxMessages.AsNoTracking().Where(m => m.AggregateId == occurrenceId).OrderBy(m => m.CreatedAtUtc).ToListAsync();
    }

    public async Task<List<InAppNotification>> NotificationRowsAsync(Guid occurrenceId)
    {
        await using var context = createContext();
        return await context.InAppNotifications.AsNoTracking().Where(n => n.OccurrenceId == occurrenceId).ToListAsync();
    }

    public async Task<int> LiveSupplyAsync(Guid requirementId)
    {
        await using var context = createContext();
        return await context.RosterAssignments.CountAsync(a =>
            a.RequirementId == requirementId && a.Status >= RosterAssignmentStatus.Reserved && a.Status <= RosterAssignmentStatus.Active);
    }

    public async Task SetEventStatusAsync(Guid occurrenceId, EventStatus status)
    {
        await using var context = createContext();
        var occurrence = await context.Events.SingleAsync(e => e.Id == occurrenceId);
        occurrence.Status = status;
        await context.SaveChangesAsync();
    }
}
