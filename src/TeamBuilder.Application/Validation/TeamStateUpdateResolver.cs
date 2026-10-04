using TeamBuilder.Application.DTOs;
using TeamBuilder.Domain.Enums;

namespace TeamBuilder.Application.Validation;

/// <summary>
/// Resolves the lifecycle and recruitment state a team update produces, translating the
/// legacy <see cref="TeamStatus"/> input and enforcing that only an Active team can accept
/// members. Invalid payloads throw <see cref="ArgumentException"/> (400).
/// </summary>
public static class TeamStateUpdateResolver
{
    public const string FullCannotBeSetMessage =
        "Full is derived from roster capacity and cannot be set directly.";

    public const string AmbiguousStatusMessage =
        "Status is a legacy field and cannot be combined with LifecycleStatus or IsAcceptingMembers.";

    public const string NotActiveCannotAcceptMessage =
        "An inactive or disbanded team cannot accept new members.";

    public static (TeamLifecycleStatus LifecycleStatus, bool IsAcceptingMembers) Resolve(
        TeamLifecycleStatus currentLifecycleStatus,
        bool currentIsAcceptingMembers,
        UpdateTeamDto update)
    {
        TeamLifecycleStatus? requestedLifecycle;
        bool? requestedAccepting;

        if (update.Status is { } legacyStatus)
        {
            if (update.LifecycleStatus.HasValue || update.IsAcceptingMembers.HasValue)
                throw new ArgumentException(AmbiguousStatusMessage, nameof(update));

            (requestedLifecycle, requestedAccepting) = legacyStatus switch
            {
                TeamStatus.Recruiting => (TeamLifecycleStatus.Active, true),
                TeamStatus.Active => (TeamLifecycleStatus.Active, false),
                TeamStatus.Inactive => (TeamLifecycleStatus.Inactive, false),
                TeamStatus.Disbanded => (TeamLifecycleStatus.Disbanded, false),
                TeamStatus.Full => throw new ArgumentException(FullCannotBeSetMessage, nameof(update)),
                _ => throw new ArgumentException($"Unknown team status '{legacyStatus}'.", nameof(update))
            };
        }
        else
        {
            requestedLifecycle = update.LifecycleStatus;
            requestedAccepting = update.IsAcceptingMembers;
        }

        var lifecycle = requestedLifecycle ?? currentLifecycleStatus;

        if (lifecycle != TeamLifecycleStatus.Active)
        {
            // Inactive/Disbanded always closes recruitment; asking to keep it open is invalid.
            if (requestedAccepting == true)
                throw new ArgumentException(NotActiveCannotAcceptMessage, nameof(update));

            return (lifecycle, false);
        }

        // Active: an omitted value keeps the current one, except that reactivating an
        // inactive/disbanded team never reopens recruitment implicitly; the owner reopens it
        // explicitly, in this request or a later one.
        var currentAccepting = currentLifecycleStatus == TeamLifecycleStatus.Active && currentIsAcceptingMembers;
        return (lifecycle, requestedAccepting ?? currentAccepting);
    }
}
