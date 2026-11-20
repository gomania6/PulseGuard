using System.IO;
using System.Runtime.InteropServices;
using PulseGuard.Core.Interfaces;

namespace PulseGuard.Modules.Optimization;

public sealed record TempTargetAnalysis(int FileCount, long TotalBytes);

public sealed record TempAnalysisResult(
    TempTargetAnalysis UserTemp,
    TempTargetAnalysis SystemTemp,
    TempTargetAnalysis RecycleBin);

/// <summary>
/// Safely analyzes and cleans user temp, Windows temp, and optionally the Recycle Bin.
/// Always wraps deletions in try-catch to silently skip locked files.
/// </summary>
public sealed class TempFilesCleanerTool : IOptimizerTool
{
    public string Id => "temp-files-cleaner";
    public string Name => "Очистка временных файлов";
    public string Description => "Безопасное удаление временных файлов пользователя, Windows Temp и Корзины с предпросмотром.";
    public bool CanExecute => true;

    public bool IncludeUserTemp { get; set; } = true;
    public bool IncludeSystemTemp { get; set; } = true;
    public bool IncludeRecycleBin { get; set; } = false;

    public static string UserTempPath => Path.GetTempPath();
    public static string SystemTempPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "Temp");

    public Task<TempAnalysisResult> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var userTemp = AnalyzeDirectory(UserTempPath, cancellationToken);
            var systemTemp = AnalyzeDirectory(SystemTempPath, cancellationToken);
            var recycleBin = AnalyzeRecycleBin();

            return new TempAnalysisResult(userTemp, systemTemp, recycleBin);
        }, cancellationToken);
    }

    public async Task<OptimizationPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var analysis = await AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        long totalBytes = 0;
        int totalFiles = 0;

        if (IncludeUserTemp)
        {
            totalBytes += analysis.UserTemp.TotalBytes;
            totalFiles += analysis.UserTemp.FileCount;
        }

        if (IncludeSystemTemp)
        {
            totalBytes += analysis.SystemTemp.TotalBytes;
            totalFiles += analysis.SystemTemp.FileCount;
        }

        if (IncludeRecycleBin)
        {
            totalBytes += analysis.RecycleBin.TotalBytes;
            totalFiles += analysis.RecycleBin.FileCount;
        }

        return new OptimizationPreview(
            $"Найдено {totalFiles} объектов ({FormatSize(totalBytes)})",
            totalBytes,
            totalFiles);
    }

    public Task<OptimizationResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            int deletedFiles = 0;
            long freedBytes = 0;

            if (IncludeUserTemp)
            {
                var (count, bytes) = CleanDirectory(UserTempPath, cancellationToken);
                deletedFiles += count;
                freedBytes += bytes;
            }

            if (IncludeSystemTemp)
            {
                var (count, bytes) = CleanDirectory(SystemTempPath, cancellationToken);
                deletedFiles += count;
                freedBytes += bytes;
            }

            if (IncludeRecycleBin)
            {
                var rb = AnalyzeRecycleBin();
                if (rb.FileCount > 0 || rb.TotalBytes > 0)
                {
                    if (EmptyRecycleBin(out var errorCode))
                    {
                        deletedFiles += rb.FileCount;
                        freedBytes += rb.TotalBytes;
                    }
                    else
                    {
                        var partial = $"Удалено {deletedFiles} файлов (освобождено {FormatSize(freedBytes)}). Ошибка очистки Корзины: 0x{errorCode:X8}";
                        return new OptimizationResult(false, partial, freedBytes, deletedFiles);
                    }
                }
            }

            var message = $"Удалено {deletedFiles} файлов (освобождено {FormatSize(freedBytes)})";
            return new OptimizationResult(true, message, freedBytes, deletedFiles);
        }, cancellationToken);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 МБ";
        }

        const double mb = 1024.0 * 1024.0;
        const double gb = mb * 1024.0;
        return bytes >= gb
            ? $"{bytes / gb:F2} ГБ"
            : $"{bytes / mb:F1} МБ";
    }

    private static TempTargetAnalysis AnalyzeDirectory(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new TempTargetAnalysis(0, 0);
        }

        int count = 0;
        long totalBytes = 0;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var info = new FileInfo(file);
                    totalBytes += info.Length;
                    count++;
                }
                catch
                {
                    // Skip inaccessible files.
                }
            }
        }
        catch
        {
            // Skip inaccessible root directory.
        }

        return new TempTargetAnalysis(count, totalBytes);
    }

    private static (int DeletedCount, long FreedBytes) CleanDirectory(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return (0, 0);
        }

        int deletedCount = 0;
        long freedBytes = 0;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var info = new FileInfo(file);
                    var length = info.Length;
                    info.IsReadOnly = false;
                    info.Delete();
                    deletedCount++;
                    freedBytes += length;
                }
                catch
                {
                    // Silently skip locked or system-protected files.
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors.
        }

        // Best-effort cleanup of empty subdirectories.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Silently skip directories containing locked files.
                }
            }
        }
        catch
        {
        }

        return (deletedCount, freedBytes);
    }

    private static TempTargetAnalysis AnalyzeRecycleBin()
    {
        return RunOnStaThread(() =>
        {
            var info = new OptimizationNativeMethods.SHQUERYRBINFO
            {
                cbSize = Marshal.SizeOf<OptimizationNativeMethods.SHQUERYRBINFO>()
            };

            try
            {
                var hr = OptimizationNativeMethods.SHQueryRecycleBinW(null, ref info);
                if (hr == 0)
                {
                    return new TempTargetAnalysis((int)Math.Max(0, info.i64NumItems), Math.Max(0, info.i64Size));
                }
            }
            catch
            {
            }

            return new TempTargetAnalysis(0, 0);
        });
    }

    private static bool EmptyRecycleBin(out int lastError)
    {
        var (success, hr) = RunOnStaThread(() =>
        {
            try
            {
                var flags = OptimizationNativeMethods.SHERB_NOCONFIRMATION
                    | OptimizationNativeMethods.SHERB_NOPROGRESSUI
                    | OptimizationNativeMethods.SHERB_NOSOUND;

                var result = OptimizationNativeMethods.SHEmptyRecycleBinW(IntPtr.Zero, null, flags);
                if (result == 0)
                {
                    return (true, 0);
                }

                // Fallback: empty per fixed drive if global null root failed.
                var anySucceeded = false;
                var lastHr = result;
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    var driveInfo = new OptimizationNativeMethods.SHQUERYRBINFO
                    {
                        cbSize = Marshal.SizeOf<OptimizationNativeMethods.SHQUERYRBINFO>()
                    };

                    if (OptimizationNativeMethods.SHQueryRecycleBinW(drive.Name, ref driveInfo) == 0
                        && (driveInfo.i64NumItems > 0 || driveInfo.i64Size > 0))
                    {
                        var driveHr = OptimizationNativeMethods.SHEmptyRecycleBinW(IntPtr.Zero, drive.Name, flags);
                        if (driveHr == 0)
                        {
                            anySucceeded = true;
                        }
                        else
                        {
                            lastHr = driveHr;
                        }
                    }
                }

                return (anySucceeded, anySucceeded ? 0 : lastHr);
            }
            catch (Exception ex)
            {
                return (false, ex.HResult);
            }
        });

        lastError = hr;
        return success;
    }

    private static T RunOnStaThread<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return action();
        }

        T result = default!;
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }

        return result;
    }
}

