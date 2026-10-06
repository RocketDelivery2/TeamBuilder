using System.Text.Json;
using TeamBuilder.Simulation;
using Xunit;

namespace TeamBuilder.Simulation.Tests;

public sealed class SimulationTests
{
    private static SimulationRunConfiguration Config(string scenario = "Tuesday5PM", long seed = 42, long users = 50_000, long concurrent = 500) =>
        new(scenario, seed, users, concurrent, TimeSpan.FromMinutes(5), 1m);

    [Fact]
    public void Same_seed_produces_identical_summary_and_workload()
    {
        var first = SimulationEngine.Run(Config());
        var second = SimulationEngine.Run(Config());
        Assert.Equal(JsonSerializer.Serialize(first.Summary), JsonSerializer.Serialize(second.Summary));
        Assert.Equal(JsonSerializer.Serialize(first.Workload), JsonSerializer.Serialize(second.Workload));
    }

    [Fact]
    public void Different_seeds_change_projected_action_distribution()
    {
        var first = SimulationEngine.Run(Config(seed: 1, users: 100_000, concurrent: 20_000));
        var second = SimulationEngine.Run(Config(seed: 2, users: 100_000, concurrent: 20_000));
        Assert.NotEqual(JsonSerializer.Serialize(first.Summary.ActionsByType), JsonSerializer.Serialize(second.Summary.ActionsByType));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(10, 0)]
    public void Invalid_population_or_concurrency_is_rejected(long population, long concurrent) =>
        Assert.Throws<ArgumentException>(() => SimulationEngine.Run(Config(users: population, concurrent: concurrent)));

    [Fact]
    public void Invalid_scenario_and_duration_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SimulationEngine.Run(Config("not-a-scenario")));
        Assert.Throws<ArgumentException>(() => SimulationEngine.Run(Config() with { Duration = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => SimulationEngine.Run(Config() with { Duration = TimeSpan.FromDays(500) }));
    }

    [Fact]
    public void Scenario_catalog_contains_required_and_future_scenarios()
    {
        var names = ScenarioCatalog.All.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("FiftyMillionPopulation", names);
        Assert.Contains("OneSpot100KClaimants", names);
        Assert.Contains("ManyTeamsDistributedLoad", names);
        Assert.Contains("RecurringEventDiscovery", names);
        Assert.Contains("JoinLeaveRefillCycles", names);
        Assert.Contains("WorldFirstRoleHotspot", names);
    }

    [Fact]
    public void Fifty_million_users_are_population_metadata_not_actor_allocations()
    {
        var result = SimulationEngine.Run(new SimulationRunConfiguration(
            "FiftyMillionPopulation", 7, 50_000_000, 2_000, TimeSpan.FromMinutes(60), 1m));
        Assert.Equal(50_000_000, result.Summary.LogicalUsers);
        Assert.Equal(2_000, result.Summary.ConcurrentUsers);
        Assert.True(result.Summary.GeneratedActions <= 100_000);
    }

    [Fact]
    public void One_spot_claimant_scenario_is_intent_only_and_does_not_claim_winners()
    {
        var result = SimulationEngine.Run(new SimulationRunConfiguration(
            "OneSpot100KClaimants", 9, 100_000, 1_000, TimeSpan.FromMinutes(1), 1m));
        Assert.Equal(100_000, result.Summary.ClaimIntents);
        Assert.Equal(100_000, result.Summary.ProjectedRequestsByEndpointClass["FutureConceptual"]);
        Assert.Null(result.Summary.ExactWinnerCount);
        Assert.Null(result.Summary.CapacityInvariantViolations);
    }

    [Fact]
    public void Histogram_has_bounded_storage_and_returns_ordered_percentiles()
    {
        var histogram = new BoundedHistogram(0, 100, 20);
        for (var i = 0; i < 1_000_000; i++) histogram.Add(i % 100);
        var p = histogram.Percentiles("ms", "synthetic");
        Assert.Equal(20, histogram.StorageBins);
        Assert.Equal(1_000_000, histogram.Count);
        Assert.True(p.P50 <= p.P95);
        Assert.True(p.P95 <= p.P99);
    }

    [Fact]
    public void Summary_json_round_trips_and_is_marked_projected()
    {
        var summary = SimulationEngine.Run(Config()).Summary;
        var json = JsonSerializer.Serialize(summary, JsonOptions.Indented);
        var copy = JsonSerializer.Deserialize<SimulationSummary>(json, JsonOptions.Indented);
        Assert.NotNull(copy);
        Assert.Equal("Projected", copy!.SimulationKind);
        Assert.Equal(JsonSerializer.Serialize(summary, JsonOptions.Indented), JsonSerializer.Serialize(copy, JsonOptions.Indented));
    }

    [Fact]
    public void Synthetic_ids_are_deterministic_and_not_personal_data()
    {
        var id = SimulationEngine.SyntheticId(42, 5);
        Assert.Equal(id, SimulationEngine.SyntheticId(42, 5));
        Assert.Equal(24, id.Length);
        Assert.DoesNotContain("@", id);
    }

    [Theory]
    [InlineData("small", 1_000)]
    [InlineData("100k", 100_000)]
    [InlineData("1m", 1_000_000)]
    [InlineData("10m", 10_000_000)]
    [InlineData("50m", 50_000_000)]
    public void Dataset_plans_are_plan_only_and_do_not_generate_rows(string scale, long players)
    {
        var plan = DatasetScalePlan.Create(scale);
        Assert.Equal(players, plan.Players);
        Assert.Equal("PlanOnly", plan.Mode);
        Assert.Equal(players >= 100_000, plan.RequiresLargeRunOptIn);
    }

    [Fact]
    public void Result_uses_projected_endpoint_classes_and_makes_no_winner_claim()
    {
        var result = SimulationEngine.Run(Config());
        Assert.Equal("Projected", result.Summary.SimulationKind);
        Assert.Contains("PublicEventList", result.Summary.ProjectedRequestsByEndpointClass.Keys);
        Assert.Null(result.Summary.ExactWinnerCount);
        Assert.Contains(result.Workload, b => b.ProjectedRequests >= 0 && b.ArrivalRatePerSecond >= 0);
    }

    [Fact]
    public void Projected_events_have_stable_chronological_order()
    {
        var result = SimulationEngine.Run(Config());
        var events = SimulationEngine.ProjectEvents(result);
        Assert.Equal(events.OrderBy(e => e.Minute).ThenBy(e => e.Sequence), events);
        Assert.Equal(events, SimulationEngine.ProjectEvents(result));
    }

    [Fact]
    public void Writer_emits_deterministic_summary_scenario_and_workload_json()
    {
        var result = SimulationEngine.Run(Config());
        var output = Path.Combine(Path.GetTempPath(), $"teambuilder-sim-{Guid.NewGuid():N}");
        try
        {
            var directory = SimulationWriter.Write(result, output);
            var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")));
            var scenarioJson = File.ReadAllText(Path.Combine(directory, "scenario.json"));
            var scenario = JsonSerializer.Deserialize<SimulationScenarioDocument>(scenarioJson, JsonOptions.Indented);
            var workload = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "workload.json")));
            Assert.Equal("Projected", summary.RootElement.GetProperty("simulationKind").GetString());
            Assert.NotNull(scenario);
            Assert.Equal(result.Scenario.Name, scenario!.Scenario.Name);
            Assert.Equal(result.Configuration, scenario.Configuration);
            Assert.Equal("Projected", workload.RootElement.GetProperty("simulationKind").GetString());
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }
}
