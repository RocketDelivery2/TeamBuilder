using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamBuilder.Simulation;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }

            return args[0] switch
            {
                "simulate" => Simulate(args[1..]),
                "plan-dataset" => PlanDataset(args[1..]),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Output failed: {ex.Message}");
            return 3;
        }
    }

    private static int Simulate(string[] args)
    {
        var options = Arguments.Parse(args);
        var scenarioName = options.Get("scenario", "Tuesday5PM");
        var seed = options.GetLong("seed", 42);
        var population = options.GetLong("logical-users", 50_000);
        var concurrency = options.GetLong("concurrent-users", Math.Max(1, population / 100));
        var minutes = options.GetInt("duration-minutes", 60);
        var timeScale = options.GetDecimal("time-scale", 1m);
        var output = options.Get("output", Path.Combine("artifacts", "performance"));
        var config = new SimulationRunConfiguration(scenarioName, seed, population, concurrency, TimeSpan.FromMinutes(minutes), timeScale);
        var result = SimulationEngine.Run(config);
        var artifactPath = SimulationWriter.Write(result, output);

        Console.WriteLine($"Simulation kind: Projected");
        Console.WriteLine($"Scenario: {result.Scenario.Name} v{result.Scenario.Version}");
        Console.WriteLine($"Logical users: {population:N0}; concurrent users: {concurrency:N0}");
        Console.WriteLine($"Projected actions: {result.Summary.GeneratedActions:N0}");
        Console.WriteLine($"Resident logical-user objects: 0");
        Console.WriteLine($"Run ID: {result.Summary.RunId}");
        Console.WriteLine($"Artifacts: {artifactPath}");
        return 0;
    }

    private static int PlanDataset(string[] args)
    {
        var options = Arguments.Parse(args);
        var scale = options.Get("scale", "small");
        var output = options.Get("output", Path.Combine("artifacts", "performance"));
        var plan = DatasetScalePlan.Create(scale);
        Directory.CreateDirectory(output);
        var target = Path.Combine(output, $"dataset-plan-{plan.Name}.json");
        File.WriteAllText(target, JsonSerializer.Serialize(plan, JsonOptions.Indented));
        Console.WriteLine($"Dataset plan '{plan.Name}' describes {plan.Players:N0} players; no rows were generated.");
        Console.WriteLine(target);
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            TeamBuilder.Simulation
              simulate [--scenario Tuesday5PM] [--seed 42] [--logical-users 50000]
                       [--concurrent-users 500] [--duration-minutes 60] [--time-scale 1]
                       [--output artifacts/performance]
              plan-dataset [--scale small|100k|1m|10m|50m] [--output artifacts/performance]

            Simulation results are projected workload models, not measured API performance.
            Dataset planning writes a plan only; it never inserts or generates SQL rows.
            """);
    }
}

public sealed record SimulationRunConfiguration(
    string ScenarioName,
    long Seed,
    long LogicalPopulation,
    long ConcurrentUsers,
    TimeSpan Duration,
    decimal TimeScale)
{
    public void Validate()
    {
        if (!ScenarioCatalog.TryGet(ScenarioName, out _))
            throw new ArgumentException($"Unknown scenario '{ScenarioName}'. See docs/performance/scenarios.md for supported scenarios.");
        if (LogicalPopulation <= 0)
            throw new ArgumentException("Logical population must be greater than zero.");
        if (ConcurrentUsers <= 0 || ConcurrentUsers > LogicalPopulation)
            throw new ArgumentException("Concurrent users must be greater than zero and no greater than logical users.");
        if (Duration <= TimeSpan.Zero || Duration.TotalMinutes > 10_000)
            throw new ArgumentException("Duration must be positive and at most 10,000 minutes.");
        if (TimeScale <= 0 || TimeScale > 1_000_000)
            throw new ArgumentException("Time scale must be positive and at most 1,000,000.");
    }
}

public enum PersonaArchetype { Player, Host, Hybrid }
public enum SimulationEventType { Search, ListTeams, ReadTeam, ListEvents, ReadEvent, CreateEvent, CreateTeam, JoinIntent, LeaveIntent, CancelIntent, ReplacementDemand, ClaimIntent, NotificationCandidate }
public enum EndpointClass
{
    Health,
    Readiness,
    PublicTeamList,
    PublicTeamDetails,
    PublicEventList,
    PublicEventDetails,
    ImplementedTeamCreate,
    ImplementedEventCreate,
    ImplementedJoinRequestCreate,
    ImplementedTeamMemberLeave,
    ImplementedEventCancel,
    FutureConceptual
}

public sealed record BehaviorWeights(
    int Search,
    int ListTeams,
    int ReadTeam,
    int ListEvents,
    int ReadEvent,
    int CreateEvent,
    int CreateTeam,
    int JoinIntent,
    int LeaveIntent,
    int CancelIntent,
    int ReplacementDemand,
    int ClaimIntent,
    int NotificationCandidate);

public sealed record SimulationScenario(string Name, int Version, string Description, BehaviorWeights Weights);
public readonly record struct SimulationClock(long Minute)
{
    public SimulationClock Advance() => new(Minute + 1);
}
public sealed record SimulationEvent(long Minute, long Sequence, SimulationEventType Type, EndpointClass EndpointClass, string Activity, string Region);
public sealed record WorkloadBucket(long Minute, EndpointClass EndpointClass, long ProjectedRequests, decimal ArrivalRatePerSecond, long ConcurrencyEstimate, IReadOnlyDictionary<string, decimal> ActivityMix, IReadOnlyDictionary<string, decimal> RegionMix);
public sealed record PercentileSet(double P50, double P95, double P99, string Unit, string Interpretation);
public sealed record SyntheticLatencyProfile(double ThinkTimeMeanMs, double NetworkDelayMeanMs, string SourceLabel);
public sealed record PersonaProfile(PersonaArchetype Archetype, string ActivityCategory, string Region, string TimeZone, decimal AvailabilityTendency, int SearchRadiusMiles, decimal HostFrequency, decimal SearchFrequency, decimal JoinFrequency, decimal LeaveProbability, decimal NoShowProbability, decimal RecurringEventFrequency, decimal Reliability, SyntheticLatencyProfile LatencyProfile);
public sealed record ActionCount(string Action, long Count);
public sealed record SimulationSummary(
    string SchemaVersion,
    string SimulationKind,
    string RunId,
    string Scenario,
    int ScenarioVersion,
    long Seed,
    long LogicalUsers,
    long ConcurrentUsers,
    string SimulationDuration,
    long GeneratedActions,
    IReadOnlyList<ActionCount> ActionsByType,
    long PeakLogicalConcurrency,
    IReadOnlyDictionary<string, long> ProjectedRequestsByEndpointClass,
    long Searches,
    long TeamReads,
    long EventReads,
    long EventWrites,
    long JoinIntents,
    long LeaveIntents,
    long Cancellations,
    long ReplacementDemandEvents,
    long ClaimIntents,
    long NotificationCandidates,
    long PotentialNotifications,
    long EstimatedNotificationRecipients,
    long EstimatedDownstreamMessages,
    PercentileSet SyntheticThinkTime,
    PercentileSet SyntheticNetworkDelay,
    long RequestCount,
    decimal ProjectedThroughputPerSecond,
    long? SuccessCount,
    long? BusinessConflictCount,
    long? TransportOrServerErrorCount,
    long? TimeoutCount,
    long? DeadlockCount,
    long? ExactWinnerCount,
    long? CapacityInvariantViolations);

public sealed record SimulationMetrics(
    IReadOnlyDictionary<SimulationEventType, long> ActionsByType,
    long ProjectedRequestCount,
    PercentileSet SyntheticThinkTime,
    PercentileSet SyntheticNetworkDelay);
public sealed record SimulationScenarioDocument(SimulationScenario Scenario, SimulationRunConfiguration Configuration);
public sealed record SimulationResult(SimulationScenario Scenario, SimulationRunConfiguration Configuration, SimulationSummary Summary, IReadOnlyList<WorkloadBucket> Workload, SimulationMetrics Metrics);
public sealed record PopulationSegment(string Activity, string Region, string TimeZone, PersonaArchetype Archetype, long LogicalUsers, decimal Weight);

public static class ScenarioCatalog
{
    private static readonly SimulationScenario[] Items =
    [
        new("Tuesday5PM", 1, "Regional after-work discovery with mixed sports and pickup activity.", new(40, 15, 10, 20, 10, 2, 1, 7, 2, 1, 1, 0, 0)),
        new("SaturdayPickupPeak", 1, "Distributed local sports discovery, event creation, and moderate churn.", new(35, 15, 8, 18, 8, 5, 3, 5, 2, 1, 2, 0, 0)),
        new("WorldFirstLaunch", 1, "Gaming launch with high read skew and role-hotspot claim intent.", new(35, 15, 10, 15, 10, 1, 1, 3, 1, 1, 1, 6, 0)),
        new("MassDeparture", 1, "Compressed departure burst with replacement and follow-up discovery demand.", new(15, 10, 5, 10, 5, 0, 0, 0, 30, 5, 20, 0, 0)),
        new("NotificationBurst", 1, "Reminder fan-out estimate and follow-up activity; no messages are sent.", new(10, 10, 5, 10, 5, 0, 0, 2, 2, 2, 2, 0, 25)),
        new("OneSpot100KClaimants", 1, "Abstract workload intent for 100,000 claimants targeting one scarce opening.", new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100, 0)),
        new("FiftyMillionPopulation", 1, "Aggregate representation of 50 million logical users without actor allocation.", new(40, 15, 10, 20, 10, 2, 1, 7, 2, 1, 1, 0, 0)),
        new("ManyTeamsDistributedLoad", 1, "Future abstraction for broadly distributed team discovery.", new(40, 20, 10, 15, 8, 2, 1, 3, 1, 0, 0, 0, 0)),
        new("OneSpotContention", 1, "Future abstract contention scenario; not a production correctness test.", new(5, 5, 5, 5, 5, 0, 0, 0, 0, 0, 0, 75, 0)),
        new("RecurringEventDiscovery", 1, "Future recurring schedule discovery workload abstraction.", new(35, 5, 5, 35, 10, 2, 1, 5, 1, 1, 0, 0, 0)),
        new("JoinLeaveRefillCycles", 1, "Future membership and refill lifecycle workload abstraction.", new(15, 10, 5, 10, 5, 1, 0, 20, 15, 4, 10, 0, 0)),
        new("WorldFirstRoleHotspot", 1, "Future role-weighted hotspot abstraction for gaming groups.", new(20, 10, 5, 10, 5, 0, 0, 5, 0, 0, 2, 40, 0))
    ];

    public static IReadOnlyList<SimulationScenario> All => Items;
    public static bool TryGet(string name, out SimulationScenario scenario)
    {
        scenario = Items.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return scenario is not null;
    }
    public static SimulationScenario Get(string name) =>
        TryGet(name, out var scenario) ? scenario : throw new ArgumentException($"Unknown scenario '{name}'.");
}

public static class SimulationEngine
{
    public static SimulationResult Run(SimulationRunConfiguration config)
    {
        config.Validate();
        var scenario = ScenarioCatalog.Get(config.ScenarioName);
        var runId = StableId($"{scenario.Name}|{scenario.Version}|{config.Seed}|{config.LogicalPopulation}|{config.ConcurrentUsers}|{config.Duration.Ticks}|{config.TimeScale}");
        var weights = scenario.Weights;
        var actionWeights = Enum.GetValues<SimulationEventType>()
            .Select(type => (Type: type, Weight: Weight(weights, type)))
            .Where(item => item.Weight > 0)
            .ToArray();
        var totalWeight = actionWeights.Sum(item => item.Weight);
        var maxConcurrency = Math.Max(1, Math.Min(config.ConcurrentUsers, checked((long)Math.Ceiling(config.ConcurrentUsers * (double)config.TimeScale))));
        var totalActions = checked((long)Math.Ceiling(config.ConcurrentUsers * config.Duration.TotalMinutes * (double)config.TimeScale * ((double)totalWeight / 100)));
        if (scenario.Name == "OneSpot100KClaimants")
            totalActions = Math.Min(100_000, config.LogicalPopulation);
        if (scenario.Name == "FiftyMillionPopulation")
            totalActions = Math.Min(totalActions, 100_000);

        var counts = actionWeights.ToDictionary(item => item.Type, _ => 0L);
        var endpointCounts = new Dictionary<EndpointClass, long>();
        var buckets = new List<WorkloadBucket>();
        var thinkHistogram = new BoundedHistogram(0, 120_000, 120);
        var networkHistogram = new BoundedHistogram(0, 10_000, 100);
        var random = new SplitMix64(config.Seed);
        var bucketCount = Math.Max(1, (int)Math.Ceiling(config.Duration.TotalMinutes));

        // A fixed-size deterministic sample estimates action mix regardless of logical population.
        var sampleCount = (int)Math.Min(10_000, totalActions);
        for (var i = 0; i < sampleCount; i++)
        {
            var type = PickAction(actionWeights, totalWeight, random.NextInt(totalWeight));
            counts[type]++;
        }
        ScaleCounts(counts, sampleCount, totalActions);
        if (scenario.Name == "OneSpot100KClaimants")
        {
            foreach (var type in counts.Keys.ToArray()) counts[type] = type == SimulationEventType.ClaimIntent ? totalActions : 0;
        }
        foreach (var (type, count) in counts)
            if (EndpointFor(type) is { } endpoint)
                endpointCounts[endpoint] = endpointCounts.GetValueOrDefault(endpoint) + count;

        var latencySampleCount = (int)Math.Min(10_000, totalActions);
        for (var i = 0; i < latencySampleCount; i++)
        {
            thinkHistogram.Add((int)(random.NextInt(120_000) + random.NextInt(120_000)) / 2);
            networkHistogram.Add((int)random.NextInt(10_000));
        }

        for (var minute = 0; minute < bucketCount; minute++)
        {
            var start = (long)minute * totalActions / bucketCount;
            var end = (long)(minute + 1) * totalActions / bucketCount;
            var count = end - start;
            foreach (var endpoint in endpointCounts.Keys.Order())
            {
                var endpointWeight = actionWeights.Where(x => EndpointFor(x.Type) == endpoint).Sum(x => x.Weight);
                var endpointRequests = count * endpointWeight / Math.Max(1, totalWeight);
                buckets.Add(new WorkloadBucket(minute, endpoint, endpointRequests, endpointRequests / 60m, Math.Min(maxConcurrency, endpointRequests), ActivityMix(scenario.Name), RegionMix(scenario.Name)));
            }
        }

        var allActions = counts.Select(kv => new ActionCount(kv.Key.ToString(), kv.Value)).OrderBy(x => x.Action, StringComparer.Ordinal).ToArray();
        var searches = counts.GetValueOrDefault(SimulationEventType.Search);
        var teamReads = counts.GetValueOrDefault(SimulationEventType.ListTeams) + counts.GetValueOrDefault(SimulationEventType.ReadTeam);
        var eventReads = counts.GetValueOrDefault(SimulationEventType.ListEvents) + counts.GetValueOrDefault(SimulationEventType.ReadEvent);
        var eventWrites = counts.GetValueOrDefault(SimulationEventType.CreateEvent);
        var projectedRequests = endpointCounts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        var summary = new SimulationSummary(
            "1.0", "Projected", runId, scenario.Name, scenario.Version, config.Seed, config.LogicalPopulation, config.ConcurrentUsers,
            config.Duration.ToString("c", CultureInfo.InvariantCulture), totalActions, allActions, maxConcurrency, projectedRequests,
            searches, teamReads, eventReads, eventWrites,
            counts.GetValueOrDefault(SimulationEventType.JoinIntent), counts.GetValueOrDefault(SimulationEventType.LeaveIntent),
            counts.GetValueOrDefault(SimulationEventType.CancelIntent), counts.GetValueOrDefault(SimulationEventType.ReplacementDemand),
            counts.GetValueOrDefault(SimulationEventType.ClaimIntent), counts.GetValueOrDefault(SimulationEventType.NotificationCandidate),
            counts.GetValueOrDefault(SimulationEventType.NotificationCandidate),
            checked(counts.GetValueOrDefault(SimulationEventType.NotificationCandidate) * 5),
            checked(counts.GetValueOrDefault(SimulationEventType.NotificationCandidate) * 5),
            thinkHistogram.Percentiles("ms", "Synthetic projected think-time; not API latency."),
            networkHistogram.Percentiles("ms", "Synthetic projected network delay; not API latency."),
            endpointCounts.Values.Sum(), config.Duration.TotalSeconds <= 0 ? 0 : endpointCounts.Values.Sum() / (decimal)config.Duration.TotalSeconds,
            null, null, null, null, null, null, null);
        var metrics = new SimulationMetrics(
            new Dictionary<SimulationEventType, long>(counts),
            endpointCounts.Values.Sum(),
            summary.SyntheticThinkTime,
            summary.SyntheticNetworkDelay);
        return new SimulationResult(scenario, config, summary, buckets, metrics);
    }

    public static IReadOnlyList<SimulationEvent> ProjectEvents(SimulationResult result, int maximumEvents = 10_000)
    {
        if (maximumEvents < 0 || maximumEvents > 10_000) throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        var total = (int)Math.Min(maximumEvents, result.Summary.GeneratedActions);
        if (total == 0) return [];
        var weighted = Enum.GetValues<SimulationEventType>()
            .Select(type => (Type: type, Weight: Weight(result.Scenario.Weights, type)))
            .Where(x => x.Weight > 0)
            .ToArray();
        var random = new SplitMix64(result.Configuration.Seed);
        var events = new List<SimulationEvent>(total);
        var clock = new SimulationClock(0);
        for (var i = 0; i < total; i++)
        {
            var type = PickAction(weighted, weighted.Sum(x => x.Weight), random.NextInt(weighted.Sum(x => x.Weight)));
            var minute = clock.Minute % Math.Max(1, (int)result.Configuration.Duration.TotalMinutes);
            events.Add(new SimulationEvent(minute, i, type, EndpointFor(type) ?? EndpointClass.FutureConceptual, "mixed", "region-a"));
            clock = clock.Advance();
        }
        return events.OrderBy(e => e.Minute).ThenBy(e => e.Sequence).ToArray();
    }

    public static string SyntheticId(long seed, long logicalOrdinal)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{logicalOrdinal}"));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 12)).ToLowerInvariant();
    private static SimulationEventType PickAction((SimulationEventType Type, int Weight)[] weights, int total, int roll)
    {
        var point = roll % total;
        foreach (var item in weights)
        {
            point -= item.Weight;
            if (point < 0) return item.Type;
        }
        return weights[^1].Type;
    }
    private static int Weight(BehaviorWeights w, SimulationEventType type) => type switch
    {
        SimulationEventType.Search => w.Search,
        SimulationEventType.ListTeams => w.ListTeams,
        SimulationEventType.ReadTeam => w.ReadTeam,
        SimulationEventType.ListEvents => w.ListEvents,
        SimulationEventType.ReadEvent => w.ReadEvent,
        SimulationEventType.CreateEvent => w.CreateEvent,
        SimulationEventType.CreateTeam => w.CreateTeam,
        SimulationEventType.JoinIntent => w.JoinIntent,
        SimulationEventType.LeaveIntent => w.LeaveIntent,
        SimulationEventType.CancelIntent => w.CancelIntent,
        SimulationEventType.ReplacementDemand => w.ReplacementDemand,
        SimulationEventType.ClaimIntent => w.ClaimIntent,
        SimulationEventType.NotificationCandidate => w.NotificationCandidate,
        _ => 0
    };
    private static void ScaleCounts(Dictionary<SimulationEventType, long> counts, int sampleCount, long targetCount)
    {
        if (sampleCount == 0 || targetCount == 0) return;
        var allocated = 0L;
        var keys = counts.Keys.Order().ToArray();
        foreach (var key in keys)
        {
            var scaled = (long)((decimal)counts[key] * targetCount / sampleCount);
            counts[key] = scaled;
            allocated += scaled;
        }
        counts[keys[0]] += targetCount - allocated;
    }
    private static EndpointClass? EndpointFor(SimulationEventType type) => type switch
    {
        SimulationEventType.ListTeams => EndpointClass.PublicTeamList,
        SimulationEventType.ListEvents => EndpointClass.PublicEventList,
        SimulationEventType.ReadEvent => EndpointClass.PublicEventDetails,
        SimulationEventType.ReadTeam => EndpointClass.PublicTeamDetails,
        SimulationEventType.Search => EndpointClass.PublicEventList,
        SimulationEventType.CreateTeam => EndpointClass.ImplementedTeamCreate,
        SimulationEventType.CreateEvent => EndpointClass.ImplementedEventCreate,
        SimulationEventType.JoinIntent => EndpointClass.ImplementedJoinRequestCreate,
        SimulationEventType.LeaveIntent => EndpointClass.ImplementedTeamMemberLeave,
        SimulationEventType.CancelIntent => EndpointClass.ImplementedEventCancel,
        SimulationEventType.ReplacementDemand or SimulationEventType.ClaimIntent or SimulationEventType.NotificationCandidate => EndpointClass.FutureConceptual,
        _ => null
    };
    private static IReadOnlyDictionary<string, decimal> ActivityMix(string scenario) =>
        scenario.Contains("WorldFirst", StringComparison.Ordinal) ? new Dictionary<string, decimal> { ["gaming"] = 0.8m, ["other"] = 0.2m } :
        new Dictionary<string, decimal> { ["pickup-sports"] = 0.6m, ["other"] = 0.4m };
    private static IReadOnlyDictionary<string, decimal> RegionMix(string scenario) =>
        scenario == "ManyTeamsDistributedLoad" ? new Dictionary<string, decimal> { ["region-a"] = 0.25m, ["region-b"] = 0.25m, ["region-c"] = 0.25m, ["region-other"] = 0.25m } :
        new Dictionary<string, decimal> { ["region-a"] = 0.6m, ["region-other"] = 0.4m };
}

public sealed class BoundedHistogram
{
    private readonly long[] _bins;
    private readonly double _minimum;
    private readonly double _maximum;
    private long _count;
    public BoundedHistogram(double minimum, double maximum, int binCount)
    {
        if (maximum <= minimum || binCount < 2) throw new ArgumentOutOfRangeException(nameof(binCount));
        _minimum = minimum;
        _maximum = maximum;
        _bins = new long[binCount];
    }
    public int StorageBins => _bins.Length;
    public long Count => _count;
    public void Add(double value)
    {
        var index = value <= _minimum ? 0 : value >= _maximum ? _bins.Length - 1 :
            Math.Min(_bins.Length - 1, (int)((value - _minimum) / (_maximum - _minimum) * _bins.Length));
        _bins[index]++;
        _count++;
    }
    public PercentileSet Percentiles(string unit, string interpretation) => new(
        At(0.50), At(0.95), At(0.99), unit, interpretation);
    private double At(double percentile)
    {
        if (_count == 0) return 0;
        var target = (long)Math.Ceiling(_count * percentile);
        long seen = 0;
        for (var i = 0; i < _bins.Length; i++)
        {
            seen += _bins[i];
            if (seen >= target) return _minimum + (i + 0.5) * (_maximum - _minimum) / _bins.Length;
        }
        return _maximum;
    }
}

public sealed class SplitMix64(long seed)
{
    private ulong _state = unchecked((ulong)seed);
    public ulong Next()
    {
        var z = (_state += 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public int NextInt(int exclusiveMaximum) => (int)(Next() % (uint)exclusiveMaximum);
}

public sealed record DatasetScalePlan(string Name, long Players, long Teams, long TeamMemberships, long EventSeries, long EventOccurrences, long Venues, long JoinRequests, string Mode, bool RequiresLargeRunOptIn, string RosterExtensionNote)
{
    public static DatasetScalePlan Create(string name)
    {
        var players = name.ToLowerInvariant() switch
        {
            "small" => 1_000L,
            "100k" => 100_000L,
            "1m" => 1_000_000L,
            "10m" => 10_000_000L,
            "50m" => 50_000_000L,
            _ => throw new ArgumentException($"Unknown dataset scale '{name}'.")
        };
        return new DatasetScalePlan(name, players, players / 100, players * 4, players / 10_000, players / 1_000, players / 100, players / 5,
            "PlanOnly", players >= 100_000, "Roster requirements and assignments are extension points; no roster implementation dependency is introduced.");
    }
}

public static class SimulationWriter
{
    public static string Write(SimulationResult result, string root)
    {
        var dir = Path.Combine(root, result.Summary.RunId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(result.Summary, JsonOptions.Indented));
        File.WriteAllText(Path.Combine(dir, "scenario.json"), JsonSerializer.Serialize(new SimulationScenarioDocument(result.Scenario, result.Configuration), JsonOptions.Indented));
        File.WriteAllText(Path.Combine(dir, "workload.json"), JsonSerializer.Serialize(new { schemaVersion = "1.0", simulationKind = "Projected", buckets = result.Workload }, JsonOptions.Indented));
        return dir;
    }
}

public static class JsonOptions
{
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}

internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _values;
    private Arguments(Dictionary<string, string?> values) => _values = values;
    public static Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Expected option, found '{args[i]}'.");
            var key = args[i][2..];
            if (key == "allow-large-run") { values[key] = null; continue; }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option '--{key}' requires a value.");
            values[key] = args[++i];
        }
        return new Arguments(values);
    }
    public bool Has(string key) => _values.ContainsKey(key);
    public string Get(string key, string fallback) => _values.TryGetValue(key, out var value) ? value! : fallback;
    public int GetInt(string key, int fallback) => !_values.TryGetValue(key, out var value) ? fallback :
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new ArgumentException($"Option '--{key}' must be an integer.");
    public long GetLong(string key, long fallback) => !_values.TryGetValue(key, out var value) ? fallback :
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new ArgumentException($"Option '--{key}' must be an integer.");
    public decimal GetDecimal(string key, decimal fallback) => !_values.TryGetValue(key, out var value) ? fallback :
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new ArgumentException($"Option '--{key}' must be a number.");
}
