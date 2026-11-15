using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class IdempotencyEntryConfiguration : IEntityTypeConfiguration<IdempotencyEntry>
{
    internal const string ScopeIndexName = "ux_idempotency_entries_user_operation_hash";

    public void Configure(EntityTypeBuilder<IdempotencyEntry> builder)
    {
        builder.ToTable("idempotency_entries", table =>
        {
            table.HasCheckConstraint("ck_idempotency_entries_operation", "operation IN (1, 2)");
            table.HasCheckConstraint("ck_idempotency_entries_key_hash", "octet_length(key_hash) = 32");
            table.HasCheckConstraint("ck_idempotency_entries_seats", "seats > 0");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Operation).HasColumnName("operation");
        builder.Property(x => x.KeyHash).HasColumnName("key_hash").IsRequired();
        builder.Property(x => x.OriginalKey).HasColumnName("original_key").IsRequired();
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.ResponseBody).HasColumnName("response_body").IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.UserId, x.Operation, x.KeyHash })
            .HasDatabaseName(ScopeIndexName)
            .IsUnique();
    }
}
