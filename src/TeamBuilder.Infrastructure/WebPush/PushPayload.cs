using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamBuilder.Infrastructure.WebPush;

/// <summary>
/// The JSON the service worker receives (encrypted end to end). Deliberately sparse: the same
/// title and body as the in-app notification (activity, role and game name; never who left,
/// an address, coordinates, distance or any identity data) and opaque ids for the deep link.
/// The game page re-reads current state and permissions after it opens; the payload grants
/// nothing and can be stale.
/// </summary>
public sealed record PushPayload
{
    public int V { get; init; } = 1;
    public required string Type { get; init; }
    public required Guid NotificationId { get; init; }
    public required Guid OccurrenceId { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }

    /// <summary>Same-origin path the click opens: the exact game, with the notification id for the open receipt.</summary>
    public required string Url { get; init; }

    /// <summary>Collapses alerts about the same game on the device.</summary>
    public required string Tag { get; init; }

    public required DateTime SentAtUtc { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static PushPayload For(Guid notificationId, Guid occurrenceId, string type, string title, string body, DateTime sentAtUtc) => new()
    {
        Type = type,
        NotificationId = notificationId,
        OccurrenceId = occurrenceId,
        Title = title,
        Body = body,
        Url = $"/games/{occurrenceId}?n={notificationId:N}",
        Tag = $"occurrence-{occurrenceId:N}",
        SentAtUtc = sentAtUtc
    };

    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);

    public static PushPayload FromUtf8Json(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<PushPayload>(json, SerializerOptions) ?? throw new JsonException("Empty push payload.");
}
