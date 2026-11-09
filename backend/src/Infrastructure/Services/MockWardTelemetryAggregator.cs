using MedicalTelemetrySync.Core.Contracts;

namespace MedicalTelemetrySync.Infrastructure.Services;

public class MockWardTelemetryAggregator : IWardTelemetryAggregator
{
    public Task<IReadOnlyList<TelemetrySnapshotDto>> FetchActiveWardSnapshotsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var mockData = new List<TelemetrySnapshotDto>
        {
            new(
                WardSource: "ICU Monitor",
                DeviceId: 88001,
                PatientIdentityHash: "SHA256_A9F2",
                BedLocationCode: "ICU-BED-04",
                DeviceModelName: "BioPulse-X1",
                HeartRateBpm: 72,
                OxygenSaturationPct: 98.50m,
                LastTelemetryTime: now.AddMinutes(-5)
            ),
            new(
                WardSource: "Emergency Bay",
                DeviceId: 88001,
                PatientIdentityHash: "SHA256_B4E1",
                BedLocationCode: "ER-TRAUMA-01",
                DeviceModelName: "OmniSentry-V2",
                HeartRateBpm: 110,
                OxygenSaturationPct: 94.20m,
                LastTelemetryTime: now.AddMinutes(-2)
            ),
            new(
                WardSource: "Telemetry Ward",
                DeviceId: 99042,
                PatientIdentityHash: "SHA256_C7D8",
                BedLocationCode: "TEL-ROOM-12",
                DeviceModelName: "PulseGuard-M",
                HeartRateBpm: 68,
                OxygenSaturationPct: 99.00m,
                LastTelemetryTime: now.AddMinutes(-10)
            )
        };

        return Task.FromResult<IReadOnlyList<TelemetrySnapshotDto>>(mockData);
    }
}