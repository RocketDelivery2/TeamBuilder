using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.HasKey(tm => tm.Id);

        builder.Property(tm => tm.Role)
            .IsRequired();

        builder.Property(tm => tm.JoinedAtUtc)
            .IsRequired();

        builder.Property(tm => tm.CreatedAtUtc)
            .IsRequired();

        builder.Property(tm => tm.RowVersion)
            .IsRowVersion();

        builder.HasIndex(tm => new { tm.TeamId, tm.PlayerId });
        builder.HasIndex(tm => tm.IsActive);
    }
}
