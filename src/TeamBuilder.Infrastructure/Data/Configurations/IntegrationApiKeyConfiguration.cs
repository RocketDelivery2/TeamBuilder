using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TeamBuilder.Domain.Entities;

namespace TeamBuilder.Infrastructure.Data.Configurations;

/// <summary>
/// Integration API keys. A key is found by its unique public prefix and verified against the
/// stored SHA-256 of the whole key; the plaintext is never stored. Cascades with the player.
/// </summary>
public class IntegrationApiKeyConfiguration : IEntityTypeConfiguration<IntegrationApiKey>
{
    public const string TableName = "IntegrationApiKeys";
    public const string KeyPrefixUniqueIndexName = "UX_IntegrationApiKeys_KeyPrefix";
    public const string PlayerIndexName = "IX_IntegrationApiKeys_PlayerId";
    public const string PlayerForeignKeyName = "FK_IntegrationApiKeys_Players_PlayerId";

    public const int NameMaxLength = 80;
    public const int KeyPrefixLength = 12;
    public const int ScopesMaxLength = 200;

    // The prefix is matched exactly (it is lowercase hex), so a binary collation keeps the
    // unique index case-sensitive whatever the database default is.
    private const string OrdinalCollation = "Latin1_General_100_BIN2";

    public void Configure(EntityTypeBuilder<IntegrationApiKey> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(k => k.Id);

        builder.Property(k => k.Name).IsRequired().HasMaxLength(NameMaxLength);
        builder.Property(k => k.KeyPrefix)
            .IsRequired()
            .HasMaxLength(KeyPrefixLength)
            .IsFixedLength()
            .IsUnicode(false)
            .UseCollation(OrdinalCollation);
        builder.Property(k => k.SecretHash).IsRequired().HasMaxLength(32).IsFixedLength();
        builder.Property(k => k.Scopes).IsRequired().HasMaxLength(ScopesMaxLength).IsUnicode(false);
        builder.Property(k => k.CreatedAtUtc).IsRequired();
        builder.Property(k => k.RowVersion).IsRowVersion();

        builder.HasOne(k => k.Player)
            .WithMany()
            .HasForeignKey(k => k.PlayerId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName(PlayerForeignKeyName);

        builder.HasIndex(k => k.KeyPrefix)
            .IsUnique()
            .HasDatabaseName(KeyPrefixUniqueIndexName);

        // An owner's key list and the per-player active key limit.
        builder.HasIndex(k => new { k.PlayerId, k.CreatedAtUtc })
            .HasDatabaseName(PlayerIndexName);
    }
}
