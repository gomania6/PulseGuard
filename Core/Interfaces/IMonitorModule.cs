namespace PulseGuard.Core.Interfaces;

/// <summary>
/// Represents a modular hardware or system monitoring sensor.
/// Implementations live under /Modules/&lt;FeatureName&gt;/ and register via DI.
/// </summary>
public interface IMonitorModule : IDisposable
{
    /// <summary>
    /// Unique module identifier.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display name of the monitoring module.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Brief description of what this module monitors.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets or sets whether the module is currently active.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Initializes underlying sensors or resources.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls the latest metrics on a background thread.
    /// </summary>
    Task UpdateAsync(CancellationToken cancellationToken = default);
}

