namespace MedicalTelemetrySync.Core.Models;

public enum DeviceOperationalStatus
{
    Monitoring,
    OfflineAlert,
    Archived
}

public class TelemetryDevice
{
    public string WardSource { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public string PatientIdentityHash { get; set; } = string.Empty;
    public string BedLocationCode { get; set; } = string.Empty;
    public string DeviceModelName { get; set; } = string.Empty;
    public int HeartRateBpm { get; set; }
    public decimal OxygenSaturationPct { get; set; }
    public DateTime LastTelemetryTimeUtc { get; set; }
    public DeviceOperationalStatus Status { get; set; } = DeviceOperationalStatus.Monitoring;
    public DateTime LastReconciledTimeUtc { get; set; }
    public Guid LastSyncBatchId { get; set; }
    public ICollection<DeviceAuditLog> AuditLogs { get; set; } = new List<DeviceAuditLog>();
}

public class DeviceAuditLog
{
    public long Id { get; set; }
    public string WardSource { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public TelemetryDevice Device { get; set; } = null!;
    public Guid SyncBatchId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string OldValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    public DateTime ChangedAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
}