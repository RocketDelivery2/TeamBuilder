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

        builder.HasIndex(jr => new { jr.TeamId, jr.PlayerId });
        builder.HasIndex(jr => jr.Status);
        builder.HasIndex(jr => jr.RequestedAtUtc);
    }
}
