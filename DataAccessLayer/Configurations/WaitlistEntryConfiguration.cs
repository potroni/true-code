using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.ToTable("waitlist_entries", table =>
        {
            table.HasCheckConstraint("ck_waitlist_entries_seats", "seats > 0");
            table.HasCheckConstraint(
                "ck_waitlist_entries_status",
                "status IN ('waiting', 'cancelled', 'promoted', 'closed')");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion(
                status => status.ToString().ToLowerInvariant(),
                value => Enum.Parse<WaitlistStatus>(value, true))
            .IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        builder.HasOne<Class>().WithMany().HasForeignKey(x => x.ClassId);

        const string waitingFilter = "status = 'waiting'";

        builder.HasIndex(x => new { x.UserId, x.ClassId })
            .HasDatabaseName("ux_waitlist_entries_waiting_user_class")
            .IsUnique()
            .HasFilter(waitingFilter);

        builder.HasIndex(x => new { x.ClassId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_waitlist_entries_waiting_fifo")
            .HasFilter(waitingFilter);
    }
}
