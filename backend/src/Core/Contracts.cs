namespace MedicalTelemetrySync.Core.Contracts;

using MedicalTelemetrySync.Core.Models;

public record TelemetrySnapshotDto(
    string WardSource,
    int DeviceId,
    string? PatientIdentityHash,
    string BedLocationCode,
    string DeviceModelName,
    int HeartRateBpm,
    decimal OxygenSaturationPct,
    DateTime LastTelemetryTime
);

public record ReconciliationSummary(
    Guid BatchId,
    int TotalProcessed,
    int InsertedCount,
    int UpdatedCount,
    int OfflineAlertsCount,
    int ArchivedCount,
    int AuditLogsWritten,
    DateTime ExecutedAtUtc
);

public interface IWardTelemetryAggregator
{
    Task<IReadOnlyList<TelemetrySnapshotDto>> FetchActiveWardSnapshotsAsync(CancellationToken ct = default);
}

public interface ITelemetryReconciliationService
{
    Task<ReconciliationSummary> ReconcileSnapshotsAsync(
        IReadOnlyList<TelemetrySnapshotDto> snapshots, 
        CancellationToken ct = default);
}