using System.Diagnostics;

namespace PulseGuard.Modules.Settings;

/// <summary>
/// Manages elevated Windows Task Scheduler autostart for PulseGuard without touching the Registry.
/// </summary>
public sealed class StartupTaskSchedulerService
{
    private const string TaskName = "PulseGuard";

    public async Task<(bool Success, string Message)> SetStartupEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string arguments;
            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return (false, "Не удалось определить путь к исполняемому файлу.");
                }

                arguments = $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\"\" /sc onlogon /rl highest /f";
            }
            else
            {
                arguments = $"/delete /tn \"{TaskName}\" /f";
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            string stdErr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                return enabled
                    ? (true, "Автозапуск через Планировщик заданий включён.")
                    : (true, "Задача автозапуска удалена из Планировщика заданий.");
            }

            // If deleting a task that does not exist, treat as cleanly disabled.
            if (!enabled)
            {
                return (true, "Задача автозапуска отсутствует в Планировщике заданий.");
            }

            string details = string.IsNullOrWhiteSpace(stdErr) ? $"код {process.ExitCode}" : stdErr.Trim();
            return (false, $"Ошибка Планировщика заданий: {details}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Не удалось обновить задачу автозапуска: {ex.Message}");
        }
    }
}

