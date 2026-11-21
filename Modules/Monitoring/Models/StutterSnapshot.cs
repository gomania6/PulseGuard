namespace PulseGuard.Modules.Monitoring.Models;

public sealed record StutterEvent(
    DateTime TimestampUtc,
    double DelayMs,
    double DpcPercent,
    int ProcessorQueueLength,
    string? CulpritProcess);

public sealed record StutterSnapshot(
    int StutterCount,
    double MaxDelayMs,
    double CurrentDeviationMs,
    double DpcPercent,
    int ProcessorQueueLength,
    StutterEvent? LastEvent);

