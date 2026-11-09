using MedicalTelemetrySync.Core.Contracts;
using MedicalTelemetrySync.Core.Models;
using MedicalTelemetrySync.Infrastructure.Persistence;
using MedicalTelemetrySync.Infrastructure.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Connection & EF Core Configuration
var connectionString = "Data Source=telemetry_sync.db;Cache=Shared;Mode=ReadWriteCreate;";

builder.Services.AddDbContext<TelemetryDbContext>(options =>
    options.UseSqlite(connectionString));

// 2. Register Application & Domain Services
builder.Services.AddScoped<IWardTelemetryAggregator, MockWardTelemetryAggregator>();
builder.Services.AddScoped<ITelemetryReconciliationService, TelemetryReconciliationService>();

// 3. Configure CORS for Angular Frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// 4. Initialize Database Schema & Apply SQLite WAL Mode with Diagnostic Logging
try
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        db.Database.EnsureCreated();

        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
    }
    Console.WriteLine("--> SQLite Database initialized successfully.");
}
catch (Exception ex)
{
    Console.WriteLine($"--> FATAL STARTUP ERROR: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    throw;
}

app.UseCors("AllowFrontend");

// 5. Minimal API Routes
var api = app.MapGroup("/api");

// GET /api/devices - Retrieves current reconciled state
api.MapGet("/devices", async Task<Ok<List<TelemetryDeviceResponse>>> (TelemetryDbContext db, CancellationToken ct) =>
{
    var devices = await db.Devices
        .AsNoTracking()
        .OrderBy(d => d.WardSource)
        .ThenBy(d => d.DeviceId)
        .Select(d => new TelemetryDeviceResponse(
            d.WardSource,
            d.DeviceId,
            d.PatientIdentityHash,
            d.BedLocationCode,
            d.DeviceModelName,
            d.HeartRateBpm,
            d.OxygenSaturationPct,
            d.LastTelemetryTimeUtc,
            d.Status.ToString(),
            d.LastReconciledTimeUtc,
            d.LastSyncBatchId
        ))
        .ToListAsync(ct);

    return TypedResults.Ok(devices);
})
.WithName("GetDevices");

// POST /api/telemetry/sync - Triggers manual ingestion & reconciliation
api.MapPost("/telemetry/sync", async Task<Results<Ok<ReconciliationSummary>, ProblemHttpResult>> (
    IWardTelemetryAggregator aggregator,
    ITelemetryReconciliationService reconciliationService,
    CancellationToken ct) =>
{
    try
    {
        var incomingSnapshots = await aggregator.FetchActiveWardSnapshotsAsync(ct);
        var summary = await reconciliationService.ReconcileSnapshotsAsync(incomingSnapshots, ct);
        return TypedResults.Ok(summary);
    }
    catch (Exception ex)
    {
        return TypedResults.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Telemetry Reconciliation Failure");
    }
})
.WithName("TriggerTelemetrySync");

// GET /api/audit-logs - Granular history inspection
api.MapGet("/audit-logs", async Task<Ok<List<DeviceAuditLogResponse>>> (TelemetryDbContext db, CancellationToken ct) =>
{
    var logs = await db.AuditLogs
        .AsNoTracking()
        .OrderByDescending(l => l.ChangedAtUtc)
        .Take(100)
        .Select(l => new DeviceAuditLogResponse(
            l.Id,
            l.WardSource,
            l.DeviceId,
            l.SyncBatchId,
            l.FieldName,
            l.OldValue,
            l.NewValue,
            l.ChangedAtUtc,
            l.Reason
        ))
        .ToListAsync(ct);

    return TypedResults.Ok(logs);
})
.WithName("GetAuditLogs");

app.Run();

// 6. Response DTO Contracts
public record TelemetryDeviceResponse(
    string WardSource,
    int DeviceId,
    string PatientIdentityHash,
    string BedLocationCode,
    string DeviceModelName,
    int HeartRateBpm,
    decimal OxygenSaturationPct,
    DateTime LastTelemetryTimeUtc,
    string Status,
    DateTime LastReconciledTimeUtc,
    Guid LastSyncBatchId
);

public record DeviceAuditLogResponse(
    long Id,
    string WardSource,
    int DeviceId,
    Guid SyncBatchId,
    string FieldName,
    string OldValue,
    string NewValue,
    DateTime ChangedAtUtc,
    string Reason
);
