using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class JoinRequestConfiguration : IEntityTypeConfiguration<JoinRequest>
{
    public void Configure(EntityTypeBuilder<JoinRequest> builder)
    {
        builder.HasKey(jr => jr.Id);

        builder.Property(jr => jr.Status)
            .IsRequired();

        builder.Property(jr => jr.Message)
            .HasMaxLength(1000);

        builder.Property(jr => jr.RequestedAtUtc)
            .IsRequired();

        builder.Property(jr => jr.CreatedAtUtc)
            .IsRequired();

        builder.Property(jr => jr.RowVersion)
            .IsRowVersion();

        // A player can only have one pending join request per team at a time.
        // Filtered on Status = 1 (RequestStatus.Pending) so historical approved,
        // rejected, or cancelled requests for the same (TeamId, PlayerId) pair
        // never collide with a new pending request (current product behavior
        // allows re-requesting after rejection/cancellation - see
        // CreateAsync_ShouldSucceed_WhenPreviousRequestWasRejected/Cancelled).
        builder.HasIndex(jr => new { jr.TeamId, jr.PlayerId })
            .IsUnique()
            .HasFilter("[Status] = 1")
            .HasDatabaseName("UX_JoinRequests_TeamId_PlayerId_Pending");

        builder.HasIndex(jr => jr.Status);
        builder.HasIndex(jr => jr.RequestedAtUtc);
    }
}
