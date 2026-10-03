using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

public class PlayerIdentityConfiguration : IEntityTypeConfiguration<PlayerIdentity>
{
    public const string IssuerSubjectIndexName = "UX_PlayerIdentities_Issuer_Subject";

    // Issuer and Subject are opaque, case-sensitive identifiers (OIDC compares them by exact
    // string match), so they use a binary collation instead of the database's default
    // (typically case-insensitive) collation.
    private const string OrdinalCollation = "Latin1_General_100_BIN2";

    public void Configure(EntityTypeBuilder<PlayerIdentity> builder)
    {
        builder.HasKey(pi => pi.Id);

        // Issuer URLs are short in practice; 512 leaves headroom while keeping the
        // (Issuer, Subject) unique index key well under SQL Server's 1700-byte limit.
        builder.Property(pi => pi.Issuer)
            .IsRequired()
            .HasMaxLength(512)
            .UseCollation(OrdinalCollation);

        // OIDC caps "sub" at 255 ASCII characters; Entra "oid" is a GUID.
        builder.Property(pi => pi.Subject)
            .IsRequired()
            .HasMaxLength(255)
            .UseCollation(OrdinalCollation);

        builder.Property(pi => pi.Provider)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(pi => pi.TenantId)
            .HasMaxLength(100);

        builder.Property(pi => pi.CreatedAtUtc)
            .IsRequired();

        builder.Property(pi => pi.RowVersion)
            .IsRowVersion();

        // An identity is subordinate to its player: deleting the player removes its links.
        builder.HasOne(pi => pi.Player)
            .WithMany(p => p.Identities)
            .HasForeignKey(pi => pi.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        // An external identity must never map to two TeamBuilder players.
        builder.HasIndex(pi => new { pi.Issuer, pi.Subject })
            .IsUnique()
            .HasDatabaseName(IssuerSubjectIndexName);

        builder.HasIndex(pi => pi.PlayerId);
    }
}
