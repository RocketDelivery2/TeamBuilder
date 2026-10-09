using System.Text.Json;
using System.Text.Json.Serialization;
using TeamBuilder.Domain.Outbox;

namespace TeamBuilder.Application.Outbox;

/// <summary>
/// The durable contract of <c>roster.vacancy.opened.v1</c>: "open quantity of one requirement
/// increased at <see cref="OccurredAtUtc"/>". It says nothing about whether the spot is still
/// open when processed, and deliberately carries no personal data: no username, display name,
/// email or identity of the departing player, and no address or coordinates. Field names are
/// camelCase JSON and <see cref="Reason"/> is its enum name; a breaking change is a new type
/// (<c>.v2</c>), never an edit of this one.
/// </summary>
public sealed record RosterVacancyOpenedV1
{
    public const string EventType = "roster.vacancy.opened.v1";

    public required Guid EventId { get; init; }
    public required Guid OccurrenceId { get; init; }
    public required Guid RosterRequirementId { get; init; }
    public required string RoleCode { get; init; }

    /// <summary>The assignment that stopped holding supply (an opaque id, not a person).</summary>
    public required Guid VacatedAssignmentId { get; init; }

    public required VacancyReason Reason { get; init; }
    public required int PreviousOpenQuantity { get; init; }
    public required int OpenQuantity { get; init; }
    public required int RequiredCount { get; init; }
    public required DateTime OccurrenceStartUtc { get; init; }
    public Guid? VenueId { get; init; }
    public required DateTime OccurredAtUtc { get; init; }

    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Deduplication key: an assignment can open capacity only once.</summary>
    public static string DeduplicationKeyFor(Guid vacatedAssignmentId) => $"{EventType}:{vacatedAssignmentId:N}";

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <exception cref="JsonException">The payload is not a valid v1 vacancy.</exception>
    public static RosterVacancyOpenedV1 FromJson(string json) =>
        JsonSerializer.Deserialize<RosterVacancyOpenedV1>(json, SerializerOptions)
        ?? throw new JsonException("Empty vacancy payload.");
}
