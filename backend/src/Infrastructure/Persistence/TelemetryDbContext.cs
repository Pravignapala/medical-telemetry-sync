using MedicalTelemetrySync.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalTelemetrySync.Infrastructure.Persistence;

public class TelemetryDbContext : DbContext
{
    public TelemetryDbContext(DbContextOptions<TelemetryDbContext> options) : base(options) { }

    public DbSet<TelemetryDevice> Devices => Set<TelemetryDevice>();
    public DbSet<DeviceAuditLog> AuditLogs => Set<DeviceAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. TelemetryDevice: Composite Key & Validation Constraints
        modelBuilder.Entity<TelemetryDevice>(entity =>
        {
            entity.HasKey(e => new { e.WardSource, e.DeviceId });

            entity.Property(e => e.WardSource)
                  .HasMaxLength(20)
                  .IsRequired();

            entity.Property(e => e.DeviceId)
                  .IsRequired();

            entity.Property(e => e.BedLocationCode)
                  .HasMaxLength(20)
                  .IsRequired();

            entity.Property(e => e.DeviceModelName)
                  .HasMaxLength(20)
                  .IsRequired();

            entity.Property(e => e.OxygenSaturationPct)
                  .HasPrecision(5, 2);

            entity.Property(e => e.Status)
                  .HasConversion<string>()
                  .HasMaxLength(20)
                  .IsRequired();

            entity.HasIndex(e => e.LastTelemetryTimeUtc);
            entity.HasIndex(e => e.Status);
        });

        // 2. DeviceAuditLog: Composite Foreign Key Relationship
        modelBuilder.Entity<DeviceAuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.WardSource)
                  .HasMaxLength(20)
                  .IsRequired();

            entity.Property(e => e.FieldName)
                  .HasMaxLength(50)
                  .IsRequired();

            entity.Property(e => e.OldValue)
                  .HasMaxLength(255);

            entity.Property(e => e.NewValue)
                  .HasMaxLength(255);

            entity.Property(e => e.Reason)
                  .HasMaxLength(100);

            entity.HasOne(e => e.Device)
                  .WithMany(d => d.AuditLogs)
                  .HasForeignKey(e => new { e.WardSource, e.DeviceId })
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.SyncBatchId);
        });
    }
}