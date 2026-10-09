namespace TeamBuilder.Application.DTOs;

/// <summary>One of the caller's in-app notifications. Only ever returned to its own player.</summary>
public class InAppNotificationDto
{
    public Guid Id { get; set; }

    /// <summary>Stable type, e.g. <c>roster.vacancy</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The game to open; the authority for the link, not the text.</summary>
    public Guid OccurrenceId { get; set; }
    public Guid? RosterRequirementId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
    public bool IsRead => ReadAtUtc is not null;
}

/// <summary>A keyset page of notifications, newest first.</summary>
public class InAppNotificationPageDto
{
    public IReadOnlyList<InAppNotificationDto> Items { get; set; } = [];

    /// <summary>Opaque; pass back as <c>cursor</c> for the next (older) page. Null on the last page.</summary>
    public string? NextCursor { get; set; }
}

public class UnreadNotificationCountDto
{
    public int UnreadCount { get; set; }
}

/// <summary>The caller's "notify me if a spot opens" state for one requirement.</summary>
public class RosterSubscriptionDto
{
    public Guid OccurrenceId { get; set; }
    public Guid RosterRequirementId { get; set; }
    public bool Subscribed { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
}

public sealed record RosterSubscriptionResult(RosterSubscriptionDto Subscription, bool Created);
