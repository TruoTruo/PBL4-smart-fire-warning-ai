namespace SmartFireApp.Models;

public sealed record SensorReading(
    string DeviceId,
    string ZoneName,
    double Temperature,
    double Humidity,
    double GasPpm,
    bool AiDetected,
    DateTime Timestamp);

public sealed record AlertRecord(
    long AlertId,
    string DeviceId,
    string ZoneName,
    string AlertType,
    string Severity,
    DateTime AlertTime,
    bool Acknowledged,
    bool SmsSent);

/// <summary>Snapshot for the 4 KPI counters in the status banner HUD.</summary>
public sealed record KpiSnapshot(
    int SensorsOnline,
    int SensorsTotal,
    int FireDetected,
    int HeatWarnings,
    int ResponseMs);
