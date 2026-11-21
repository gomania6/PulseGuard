namespace PulseGuard.Core.Interfaces;

/// <summary>
/// Preview result before executing a safe optimization action.
/// </summary>
public sealed record OptimizationPreview(
    string Summary,
    long EstimatedReclaimableBytes,
    int AffectedItemCount);

/// <summary>
/// Outcome of executing a safe optimization tool.
/// </summary>
public sealed record OptimizationResult(
    bool Success,
    string Message,
    long ReclaimedBytes,
    int ProcessedItemCount);

/// <summary>
/// Represents a modular safe optimization tool.
/// Implementations live under /Modules/&lt;FeatureName&gt;/ and must respect ProcessBlacklist.
/// </summary>
public interface IOptimizerTool
{
    /// <summary>
    /// Unique tool identifier.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display name of the optimization tool.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Brief description of what this tool safely optimizes.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Indicates whether the tool can currently be executed.
    /// </summary>
    bool CanExecute { get; }

    /// <summary>
    /// Calculates a non-destructive preview (e.g., reclaimable MB) before execution.
    /// </summary>
    Task<OptimizationPreview> PreviewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the safe optimization action.
    /// </summary>
    Task<OptimizationResult> ExecuteAsync(CancellationToken cancellationToken = default);
}

