using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class ClassConfiguration : IEntityTypeConfiguration<Class>
{
    public void Configure(EntityTypeBuilder<Class> builder)
    {
        builder.ToTable("classes");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RoomId).HasColumnName("room_id");
        builder.Property(x => x.Title).HasColumnName("title").IsRequired();
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.DurationMinutes).HasColumnName("duration_minutes");
        builder.Property(x => x.Capacity).HasColumnName("capacity");
        builder.Property(x => x.IsCancelled).HasColumnName("is_cancelled");

        const string scheduledFilter = "NOT is_cancelled";

        builder.HasIndex(x => x.RoomId);

        builder.HasIndex(x => new { x.StartsAt, x.Id })
            .HasDatabaseName("ix_classes_scheduled_start_id")
            .HasFilter(scheduledFilter);

        builder.HasIndex(x => new { x.RoomId, x.StartsAt, x.Id })
            .HasDatabaseName("ix_classes_scheduled_room_start_id")
            .HasFilter(scheduledFilter);

        builder
            .HasOne(x => x.Room)
            .WithMany()
            .HasForeignKey(x => x.RoomId);

        builder.HasQueryFilter(x => !x.IsCancelled);
    }
}
