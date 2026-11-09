using FluentAssertions;
using MedicalTelemetrySync.Core.Contracts;
using MedicalTelemetrySync.Core.Models;
using MedicalTelemetrySync.Infrastructure.Persistence;
using MedicalTelemetrySync.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MedicalTelemetrySync.Tests;

public class TelemetryReconciliationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TelemetryDbContext _dbContext;
    private readonly TelemetryReconciliationService _sut;

    public TelemetryReconciliationServiceTests()
    {
        // Open connection keeps the in-memory SQLite schema alive across operations
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new TelemetryDbContext(options);
        _dbContext.Database.EnsureCreated();

        _sut = new TelemetryReconciliationService(_dbContext);
    }

    [Fact]
    public async Task ReconcileSnapshots_WhenFreshDevicesProvided_InsertsAllWithMonitoringStatus()
    {
        // Arrange
        var snapshots = new List<TelemetrySnapshotDto>
        {
            new("ICU Monitor", 101, "HASH_1", "ICU-01", "BioPulse-X1", 75, 98.0m, DateTime.UtcNow),
            new("Emergency Bay", 101, "HASH_2", "ER-01", "OmniSentry-V2", 95, 94.5m, DateTime.UtcNow)
        };

        // Act
        var result = await _sut.ReconcileSnapshotsAsync(snapshots);

        // Assert
        result.InsertedCount.Should().Be(2);
        result.UpdatedCount.Should().Be(0);
        result.AuditLogsWritten.Should().Be(0);

        var devices = await _dbContext.Devices.ToListAsync();
        devices.Should().HaveCount(2);
        devices.Should().OnlyContain(d => d.Status == DeviceOperationalStatus.Monitoring);
    }

    [Fact]
    public async Task ReconcileSnapshots_WhenExecutedSuccessivelyWithIdenticalPayload_ProducesZeroMutationsAndLogs()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var snapshots = new List<TelemetrySnapshotDto>
        {
            new("ICU Monitor", 101, "HASH_1", "ICU-01", "BioPulse-X1", 72, 98.5m, now),
            new("Telemetry Ward", 202, "HASH_3", "TEL-05", "PulseGuard-M", 80, 99.0m, now)
        };

        // First Ingestion Pass
        var firstRun = await _sut.ReconcileSnapshotsAsync(snapshots);
        firstRun.InsertedCount.Should().Be(2);
        firstRun.AuditLogsWritten.Should().Be(0);

        // Act - Second Pass with exact duplicate snapshot
        var secondRun = await _sut.ReconcileSnapshotsAsync(snapshots);

        // Assert - Strict Idempotency Invariants
        secondRun.InsertedCount.Should().Be(0, "No duplicate records should be created");
        secondRun.UpdatedCount.Should().Be(0, "No drift occurred; updates must remain zero");
        secondRun.OfflineAlertsCount.Should().Be(0);
        secondRun.AuditLogsWritten.Should().Be(0, "Zero audit records should be emitted on identical payloads");

        var totalDevices = await _dbContext.Devices.CountAsync();
        totalDevices.Should().Be(2);

        var totalLogs = await _dbContext.AuditLogs.CountAsync();
        totalLogs.Should().Be(0);
    }

    [Fact]
    public async Task ReconcileSnapshots_WhenVitalDriftOccurs_UpdatesStateAndWritesAuditLog()
    {
        // Arrange: Seed initial state
        var initialTime = DateTime.UtcNow.AddMinutes(-5);
        var initialPayload = new List<TelemetrySnapshotDto>
        {
            new("ICU Monitor", 101, "HASH_1", "ICU-01", "BioPulse-X1", 72, 98.00m, initialTime)
        };
        await _sut.ReconcileSnapshotsAsync(initialPayload);

        // Act: Pulse drifts from 72 -> 88 and SpO2 from 98.00 -> 93.50
        var driftedTime = DateTime.UtcNow;
        var updatedPayload = new List<TelemetrySnapshotDto>
        {
            new("ICU Monitor", 101, "HASH_1", "ICU-01", "BioPulse-X1", 88, 93.50m, driftedTime)
        };
        var result = await _sut.ReconcileSnapshotsAsync(updatedPayload);

        // Assert
        result.InsertedCount.Should().Be(0);
        result.UpdatedCount.Should().Be(1);
        result.AuditLogsWritten.Should().Be(2);

        var updatedDevice = await _dbContext.Devices.FindAsync("ICU Monitor", 101);
        updatedDevice!.HeartRateBpm.Should().Be(88);
        updatedDevice.OxygenSaturationPct.Should().Be(93.50m);

        var auditLogs = await _dbContext.AuditLogs.Where(l => l.DeviceId == 101).ToListAsync();
        auditLogs.Should().HaveCount(2);
        auditLogs.Should().Contain(l => l.FieldName == "HeartRateBpm" && l.OldValue == "72" && l.NewValue == "88");
        auditLogs.Should().Contain(l => l.FieldName == "OxygenSaturationPct" && l.OldValue == "98.00" && l.NewValue == "93.50");
    }

    [Fact]
    public async Task ReconcileSnapshots_WhenDeviceOmittedFromSnapshot_TransitionsToOfflineAlert()
    {
        // Arrange: Seed device recorded 15 minutes ago
        var recentTime = DateTime.UtcNow.AddMinutes(-15);
        var initialPayload = new List<TelemetrySnapshotDto>
        {
            new("Emergency Bay", 505, "HASH_9", "ER-BAY-02", "OmniSentry-V2", 82, 97.0m, recentTime)
        };
        await _sut.ReconcileSnapshotsAsync(initialPayload);

        // Act: Empty payload simulates device dropped from network
        var emptySnapshot = new List<TelemetrySnapshotDto>();
        var result = await _sut.ReconcileSnapshotsAsync(emptySnapshot);

        // Assert
        result.OfflineAlertsCount.Should().Be(1);
        result.AuditLogsWritten.Should().Be(1);

        var device = await _dbContext.Devices.FindAsync("Emergency Bay", 505);
        device!.Status.Should().Be(DeviceOperationalStatus.OfflineAlert);

        var auditLog = await _dbContext.AuditLogs.SingleAsync(l => l.DeviceId == 505);
        auditLog.FieldName.Should().Be("Status");
        auditLog.OldValue.Should().Be("Monitoring");
        auditLog.NewValue.Should().Be("OfflineAlert");
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }
}