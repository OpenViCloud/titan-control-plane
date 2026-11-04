using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Acceptance;

namespace Titan.Infrastructure.Persistence;

public class AcceptanceConfiguration : IEntityTypeConfiguration<Acceptance>
{
    public void Configure(EntityTypeBuilder<Acceptance> builder)
    {
        builder.ToTable("legal_acceptances");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id");
        builder.Property(a => a.KeycloakUserId).HasColumnName("subject_id").IsRequired();
        builder.Property(a => a.DocumentKey).HasColumnName("document_key").IsRequired();
        builder.Property(a => a.DocumentVersion).HasColumnName("document_version").IsRequired();
        builder.Property(a => a.AcceptedAt).HasColumnName("accepted_at").IsRequired();
        builder.Property(a => a.SourceIp).HasColumnName("source_ip"); // nullable

        // One acceptance per Keycloak user per document version, append-only
        // (titan-ops features/legal/design.md#acceptance-append-only): the DB guard that keeps RecordAcceptance
        // idempotent under a double-submit race. InMemory does not enforce it; AcceptanceSchemaTests pins it.
        builder.HasIndex(a => new { a.KeycloakUserId, a.DocumentKey, a.DocumentVersion }).IsUnique();
    }
}
