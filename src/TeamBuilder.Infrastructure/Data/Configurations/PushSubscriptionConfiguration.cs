using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

/// <summary>
/// Browser push subscriptions. The endpoint is unique through its SHA-256 (an endpoint URL can
/// exceed the 1700-byte index key limit). Cascades with the player.
/// </summary>
public class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public const string TableName = "PushSubscriptions";
    public const string EndpointHashUniqueIndexName = "UX_PushSubscriptions_EndpointHash";
    public const string PlayerActiveIndexName = "IX_PushSubscriptions_PlayerId_Active";
    public const string PlayerForeignKeyName = "FK_PushSubscriptions_Players_PlayerId";
    public const string FailureCountCheckName = "CK_PushSubscriptions_FailureCount_NonNegative";

    public const int EndpointMaxLength = 2048;
    public const int P256dhMaxLength = 128;
    public const int AuthMaxLength = 64;
    public const int UserAgentFamilyMaxLength = 40;
    public const int DisabledReasonMaxLength = 40;

    public void Configure(EntityTypeBuilder<PushSubscription> builder)
    {
        builder.ToTable(TableName, t => t.HasCheckConstraint(FailureCountCheckName, "[FailureCount] >= 0"));
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Endpoint).IsRequired().HasMaxLength(EndpointMaxLength).IsUnicode(false);
        builder.Property(s => s.EndpointHash).IsRequired().HasMaxLength(32).IsFixedLength();
        builder.Property(s => s.P256dh).IsRequired().HasMaxLength(P256dhMaxLength).IsUnicode(false);
        builder.Property(s => s.Auth).IsRequired().HasMaxLength(AuthMaxLength).IsUnicode(false);
        builder.Property(s => s.UserAgentFamily).HasMaxLength(UserAgentFamilyMaxLength).IsUnicode(false);
        builder.Property(s => s.DisabledReason).HasMaxLength(DisabledReasonMaxLength).IsUnicode(false);
        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.RowVersion).IsRowVersion();

        builder.HasOne(s => s.Player)
            .WithMany()
            .HasForeignKey(s => s.PlayerId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName(PlayerForeignKeyName);

        builder.HasIndex(s => s.EndpointHash)
            .IsUnique()
            .HasDatabaseName(EndpointHashUniqueIndexName);

        // Fan-out ("this player's active devices") and the per-player device limit.
        builder.HasIndex(s => new { s.PlayerId, s.LastSeenAtUtc })
            .HasDatabaseName(PlayerActiveIndexName)
            .HasFilter("[IsActive] = 1");
    }
}
