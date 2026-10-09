namespace TeamBuilder.Domain.Entities;

/// <summary>
/// "Notify me if a spot opens": a player's explicit opt-in to in-app alerts when capacity opens
/// on one roster requirement of one occurrence. Tied to the requirement, never to a hard-coded
/// role, so a multi-role game (tank/healer/dps, goalkeeper) is subscribed per role. It reserves
/// nothing and never joins anyone; the atomic claim stays the only way onto a roster.
/// </summary>
public class OccurrenceRosterSubscription : BaseEntity
{
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;

    public Guid OccurrenceId { get; set; }

    /// <summary>A requirement of <see cref="OccurrenceId"/> (composite foreign key).</summary>
    public Guid RosterRequirementId { get; set; }
    public RosterRequirement RosterRequirement { get; set; } = null!;
}
