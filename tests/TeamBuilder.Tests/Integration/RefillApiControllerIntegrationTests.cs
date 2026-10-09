using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBuilder.Application.DTOs;
using TeamBuilder.Application.Outbox;
using TeamBuilder.Domain.Entities;
using TeamBuilder.Infrastructure.Data;

namespace TeamBuilder.Tests.Integration;

/// <summary>
/// HTTP contract of "notify me" subscriptions and the caller's notifications (in-memory
/// database): authentication and linking order, not-found rules, per-caller rate limiting, and
/// that a departure stages its vacancy message without delivering anything in the request.
/// Transactions, races and the worker are covered against SQL Server elsewhere.
/// </summary>
public sealed class RefillApiControllerIntegrationTests : IClassFixture<TeamBuilderWebApplicationFactory>
{
    private readonly TeamBuilderWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly RefillHarness _h;

    public RefillApiControllerIntegrationTests(TeamBuilderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _h = new RefillHarness(factory, _client, () => factory.Services.CreateScope().ServiceProvider.GetRequiredService<TeamBuilderDbContext>());
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Subscription_RequiresALinkedPlayer_ThenAnExistingOccurrenceAndRequirement(string method)
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 2);
        var otherRequirement = (await _h.SeedGameAsync(requiredCount: 2)).RequirementId;
        var (_, linked) = await _h.NewPlayerAsync();
        var unlinked = TeamBuilderWebApplicationFactory.CreateTestJwt(LinkedPlayerTokens.NewSubject());
        var verb = new HttpMethod(method);
        var url = RefillHarness.SubscriptionUrl(occurrenceId, requirementId);

        (await _h.SendAsync(verb, url, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _h.SendAsync(verb, url, unlinked)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _h.SendAsync(verb, RefillHarness.SubscriptionUrl(Guid.NewGuid(), requirementId), linked)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _h.SendAsync(verb, RefillHarness.SubscriptionUrl(occurrenceId, otherRequirement), linked)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _h.SendAsync(verb, url, linked)).StatusCode.Should().Be(method == "PUT" ? HttpStatusCode.Created : HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Subscribe_ReturnsCreatedThenOk_AndTheDetailShowsItOnlyToTheCaller()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 1);
        await _h.FillAsync(occurrenceId, requirementId, 1);
        var (_, token) = await _h.NewPlayerAsync();
        var (_, other) = await _h.NewPlayerAsync();

        var created = await _h.SubscribeAsync(token, occurrenceId, requirementId);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<RosterSubscriptionDto>())!.Subscribed.Should().BeTrue();
        (await _h.SubscribeAsync(token, occurrenceId, requirementId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _h.DetailAsync(occurrenceId, token)).MySubscribedRequirementIds.Should().Equal(requirementId);
        (await _h.DetailAsync(occurrenceId, other)).MySubscribedRequirementIds.Should().BeEmpty();
        (await _h.DetailAsync(occurrenceId)).MySubscribedRequirementIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Notifications_RequireALinkedPlayer()
    {
        var unlinked = TeamBuilderWebApplicationFactory.CreateTestJwt(LinkedPlayerTokens.NewSubject());
        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, "/api/v1/players/me/notifications"),
            (HttpMethod.Get, "/api/v1/players/me/notifications/unread-count"),
            (HttpMethod.Post, $"/api/v1/players/me/notifications/{Guid.NewGuid()}/read")
        })
        {
            (await _h.SendAsync(method, url, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, url);
            (await _h.SendAsync(method, url, unlinked)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
        }

        var (_, linked) = await _h.NewPlayerAsync();
        (await _h.NotificationsAsync(linked)).Items.Should().BeEmpty();
        (await _h.UnreadCountAsync(linked)).Should().Be(0);
        (await _h.MarkReadAsync(linked, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Notifications_OutOfRangePageSize_FallsBackToTheDefault()
    {
        var (playerId, token) = await _h.NewPlayerAsync();
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 1);
        await using (var context = _h.CreateContext())
        {
            for (var i = 0; i < 25; i++)
            {
                context.InAppNotifications.Add(new InAppNotification
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    Type = "roster.vacancy",
                    OccurrenceId = occurrenceId,
                    RosterRequirementId = requirementId,
                    SourceEventId = Guid.NewGuid(),
                    Title = "Basketball spot opened",
                    Body = "A participant spot opened in Wednesday Basketball."
                });
            }
            await context.SaveChangesAsync();
        }

        var page = await _h.NotificationsAsync(token, pageSize: 500);
        page.Items.Should().HaveCount(20);
        page.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task Notifications_InvalidCursor_Is400()
    {
        var (_, token) = await _h.NewPlayerAsync();
        (await _h.SendAsync(HttpMethod.Get, "/api/v1/players/me/notifications?cursor=not-a-cursor", token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Leave_StagesTheVacancyMessage_AndDeliversNothingInTheRequest()
    {
        var (_, _, occurrenceId, requirementId) = await _h.SeedGameAsync(requiredCount: 2);
        var holder = (await _h.FillAsync(occurrenceId, requirementId, 2))[0];
        var (_, subscriber) = await _h.NewPlayerAsync();
        await _h.SubscribeAsync(subscriber, occurrenceId, requirementId);

        (await _h.LeaveAsync(holder.Token, occurrenceId, holder.AssignmentId)).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = _h.CreateContext();
        var message = await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.AggregateId == occurrenceId);
        message.Type.Should().Be(RosterVacancyOpenedV1.EventType);
        (await context.InAppNotifications.AnyAsync(n => n.OccurrenceId == occurrenceId)).Should().BeFalse();
        (await _h.UnreadCountAsync(subscriber)).Should().Be(0);
    }

    [Fact]
    public async Task SubscriptionChanges_AreRateLimitedPerCaller_With429AndRetryAfter()
    {
        await using var limited = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Subscriptions:PermitLimit"] = "2",
                ["RateLimiting:Subscriptions:WindowSeconds"] = "3600"
            })));
        using var client = limited.CreateClient();
        var first = await LinkedPlayerTokens.ForPlayerAsync(limited.Services, Guid.NewGuid());
        var second = await LinkedPlayerTokens.ForPlayerAsync(limited.Services, Guid.NewGuid());
        var url = RefillHarness.SubscriptionUrl(Guid.NewGuid(), Guid.NewGuid());

        for (var i = 0; i < 2; i++)
            (await SendAsync(client, HttpMethod.Put, url, first)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rejected = await SendAsync(client, HttpMethod.Delete, url, first);
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull();
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("notification changes");

        // Another caller has their own window; reads are not limited.
        (await SendAsync(client, HttpMethod.Put, url, second)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Get, "/api/v1/players/me/notifications/unread-count", first)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string token)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
