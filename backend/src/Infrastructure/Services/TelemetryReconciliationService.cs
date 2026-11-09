using MedicalTelemetrySync.Core.Contracts;
using MedicalTelemetrySync.Core.Models;
using MedicalTelemetrySync.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MedicalTelemetrySync.Infrastructure.Services;

public class TelemetryReconciliationService : ITelemetryReconciliationService
{
    private readonly TelemetryDbContext _dbContext;

    public TelemetryReconciliationService(TelemetryDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ReconciliationSummary> ReconcileSnapshotsAsync(
        IReadOnlyList<TelemetrySnapshotDto> incomingSnapshots,
        CancellationToken ct = default)
    {
        var batchId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;

        int inserted = 0;
        int updated = 0;
        int offlineAlerts = 0;
        int archived = 0;
        var pendingAuditLogs = new List<DeviceAuditLog>();

        // Load active operational devices into memory indexed by composite key
        var existingDevices = await _dbContext.Devices
            .Include(d => d.AuditLogs)
            .ToDictionaryAsync(d => (d.WardSource, d.DeviceId), ct);

        var incomingKeySet = incomingSnapshots
            .Select(s => (s.WardSource, s.DeviceId))
            .ToHashSet();

        // 1. Process incoming device records
        foreach (var incoming in incomingSnapshots)
        {
            var key = (incoming.WardSource, incoming.DeviceId);

            if (!existingDevices.TryGetValue(key, out var device))
            {
                // Acceptance Rule 1: Fresh Insertion
                var newDevice = new TelemetryDevice
                {
                    WardSource = incoming.WardSource,
                    DeviceId = incoming.DeviceId,
                    PatientIdentityHash = incoming.PatientIdentityHash ?? string.Empty,
                    BedLocationCode = incoming.BedLocationCode,
                    DeviceModelName = incoming.DeviceModelName,
                    HeartRateBpm = incoming.HeartRateBpm,
                    OxygenSaturationPct = incoming.OxygenSaturationPct,
                    LastTelemetryTimeUtc = incoming.LastTelemetryTime,
                    Status = DeviceOperationalStatus.Monitoring,
                    LastReconciledTimeUtc = nowUtc,
                    LastSyncBatchId = batchId
                };

                _dbContext.Devices.Add(newDevice);
                inserted++;
                continue;
            }

            // Exclude immutable archived devices
            if (device.Status == DeviceOperationalStatus.Archived)
            {
                continue;
            }

            // Acceptance Rule 2: Field Drift Reconciliation & Idempotency Check
            bool hasChanged = false;

            if (device.HeartRateBpm != incoming.HeartRateBpm)
            {
                pendingAuditLogs.Add(CreateAuditRecord(batchId, device.WardSource, device.DeviceId,
                    nameof(device.HeartRateBpm), device.HeartRateBpm.ToString(), incoming.HeartRateBpm.ToString(), nowUtc, "Vital Shift"));
                device.HeartRateBpm = incoming.HeartRateBpm;
                hasChanged = true;
            }

            if (device.OxygenSaturationPct != incoming.OxygenSaturationPct)
            {
                pendingAuditLogs.Add(CreateAuditRecord(batchId, device.WardSource, device.DeviceId,
                    nameof(device.OxygenSaturationPct), device.OxygenSaturationPct.ToString("F2"), incoming.OxygenSaturationPct.ToString("F2"), nowUtc, "Vital Shift"));
                device.OxygenSaturationPct = incoming.OxygenSaturationPct;
                hasChanged = true;
            }

            if (device.BedLocationCode != incoming.BedLocationCode)
            {
                pendingAuditLogs.Add(CreateAuditRecord(batchId, device.WardSource, device.DeviceId,
                    nameof(device.BedLocationCode), device.BedLocationCode, incoming.BedLocationCode, nowUtc, "Bed Transfer"));
                device.BedLocationCode = incoming.BedLocationCode;
                hasChanged = true;
            }

            if (device.Status != DeviceOperationalStatus.Monitoring)
            {
                pendingAuditLogs.Add(CreateAuditRecord(batchId, device.WardSource, device.DeviceId,
                    nameof(device.Status), device.Status.ToString(), DeviceOperationalStatus.Monitoring.ToString(), nowUtc, "Device Reconnected"));
                device.Status = DeviceOperationalStatus.Monitoring;
                hasChanged = true;
            }

            if (hasChanged)
            {
                device.LastTelemetryTimeUtc = incoming.LastTelemetryTime;
                device.LastReconciledTimeUtc = nowUtc;
                device.LastSyncBatchId = batchId;
                updated++;
            }
        }

        // 2. Detect missing devices & handle lifecycle transitions
        foreach (var (key, device) in existingDevices)
        {
            if (incomingKeySet.Contains(key)) continue;

            // Acceptance Rule 4: Historical telemetry finalization (> 24 hours old)
            if (device.Status != DeviceOperationalStatus.Archived &&
                (nowUtc - device.LastTelemetryTimeUtc).TotalHours >= 24)
            {
                pendingAuditLogs.Add(CreateAuditRecord(batchId, device.WardSource, device.DeviceId,
                    nameof(device.Status), device.Status.ToString(), DeviceOperationalStatus.Archived.ToString(), nowUtc, "24-Hour Expiration"));
                device.Status = DeviceOperationalStatus.Archived;
                device.LastReconciledTimeUtc = nowUtc;
                archived++;
                continue;
            }

            // Acceptance Rule 3: Network Drop-off Detection (< 1 hour old missing from snapshot)
            if (device.Status == DeviceOperationalStatus.Monitoring &&
                (nowUtc - device.LastTelemetryTimeUtc).TotalHours < 1)
            {
                pendingAuditLogs.Add(CreateAuditRecord(batchId, device.WardSource, device.DeviceId,
                    nameof(device.Status), device.Status.ToString(), DeviceOperationalStatus.OfflineAlert.ToString(), nowUtc, "Missing from Ward Payload"));
                device.Status = DeviceOperationalStatus.OfflineAlert;
                device.LastReconciledTimeUtc = nowUtc;
                offlineAlerts++;
            }
        }

        // Persist only mutated state and new audit records
        if (pendingAuditLogs.Count > 0)
        {
            await _dbContext.AuditLogs.AddRangeAsync(pendingAuditLogs, ct);
        }

        await _dbContext.SaveChangesAsync(ct);

        return new ReconciliationSummary(
            batchId,
            incomingSnapshots.Count,
            inserted,
            updated,
            offlineAlerts,
            archived,
            pendingAuditLogs.Count,
            nowUtc
        );
    }

    private static DeviceAuditLog CreateAuditRecord(
        Guid batchId, string ward, int deviceId, string field, string oldVal, string newVal, DateTime now, string reason) =>
        new()
        {
            SyncBatchId = batchId,
            WardSource = ward,
            DeviceId = deviceId,
            FieldName = field,
            OldValue = oldVal,
            NewValue = newVal,
            ChangedAtUtc = now,
            Reason = reason
        };
}