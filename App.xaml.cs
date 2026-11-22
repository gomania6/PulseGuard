using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PulseGuard.Core.Interfaces;
using PulseGuard.Modules.BatteryDevices;
using PulseGuard.Modules.Discord;
using PulseGuard.Modules.InputLayout;
using PulseGuard.Modules.Monitoring;
using PulseGuard.Modules.Network;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Overlay;
using PulseGuard.Modules.Playtime;
using PulseGuard.Modules.Processes;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Vpn;
using PulseGuard.Services;
using PulseGuard.ViewModels;
using PulseGuard.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;

namespace PulseGuard;

/// <summary>
/// Application entry point and Dependency Injection composition root.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\PulseGuard_SingleInstance_Mutex";
    private const string ShowWindowEventName = @"Local\PulseGuard_ShowWindow_Event";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private CancellationTokenSource? _singleInstanceCts;

    public IServiceProvider Services { get; }

    public App()
    {
        Services = ConfigureServices();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Navigation & UI services
        services.AddSingleton<INavigationViewPageProvider, PageService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<TrayNotificationService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<LocalizationService>();

        // Settings
        services.AddSingleton<SettingsService>();
        services.AddSingleton<StartupTaskSchedulerService>();

        // Modules
        services.AddSingleton<SystemMonitorModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<SystemMonitorModule>());
        services.AddSingleton<MonitoringPollingService>();
        services.AddSingleton<StutterMonitorModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<StutterMonitorModule>());

        services.AddSingleton<ProcessMonitorModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<ProcessMonitorModule>());
        services.AddSingleton<ProcessesPollingService>();

        services.AddSingleton<AntiCheatDetectorService>();
        services.AddSingleton<RegistryBackupService>();
        services.AddSingleton<OptimizationProfileService>();
        services.AddSingleton<SafeRamCleanerTool>();
        services.AddSingleton<IOptimizerTool>(sp => sp.GetRequiredService<SafeRamCleanerTool>());
        services.AddSingleton<RamAutoCleanSchedulerModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<RamAutoCleanSchedulerModule>());
        services.AddSingleton<TempFilesCleanerTool>();
        services.AddSingleton<IOptimizerTool>(sp => sp.GetRequiredService<TempFilesCleanerTool>());
        services.AddSingleton<NetworkOptimizerTool>();
        services.AddSingleton<IOptimizerTool>(sp => sp.GetRequiredService<NetworkOptimizerTool>());
        services.AddSingleton<GameModeTool>();
        services.AddSingleton<IOptimizerTool>(sp => sp.GetRequiredService<GameModeTool>());
        services.AddSingleton<AutoGameModeService>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<AutoGameModeService>());

        // Overlay
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<OverlayService>();

        // VPN
        services.AddSingleton<VpnService>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<VpnService>());

        // Battery Devices
        services.AddSingleton<BatteryDeviceMonitorModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<BatteryDeviceMonitorModule>());

        // Discord RPC
        services.AddSingleton<DiscordRpcModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<DiscordRpcModule>());

        // Network Radar
        services.AddSingleton<NetworkRadarModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<NetworkRadarModule>());

        // Input Layout & Playtime Tracker
        services.AddSingleton<InputLayoutModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<InputLayoutModule>());
        services.AddSingleton<PlaytimeTrackerModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<PlaytimeTrackerModule>());

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MonitoringViewModel>();
        services.AddSingleton<OptimizationViewModel>();
        services.AddSingleton<GameOptimizationViewModel>();
        services.AddSingleton<VpnViewModel>();
        services.AddSingleton<ProcessesViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // Views & Pages
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MonitoringPage>();
        services.AddSingleton<OptimizationPage>();
        services.AddSingleton<GameOptimizationPage>();
        services.AddSingleton<VpnPage>();
        services.AddSingleton<ProcessesPage>();
        services.AddSingleton<SettingsPage>();

        return services.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
                showEvent.Set();
            }
            catch
            {
                // Ignore if the primary instance has not initialized the event yet or is exiting.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _singleInstanceCts = new CancellationTokenSource();

        try
        {
            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PulseGuard");
            Directory.CreateDirectory(appDataDir);
        }
        catch
        {
            // Ignore if AppData directory creation fails.
        }

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        var settingsService = Services.GetRequiredService<SettingsService>();
        Services.GetRequiredService<LocalizationService>().ApplyLanguage(settingsService.Current.Language);
        SessionEnding += (_, _) =>
        {
            (Services.GetService(typeof(PlaytimeTrackerModule)) as PlaytimeTrackerModule)?.FlushSave();
            settingsService.SaveSettings();
        };

        var monitoringPollingService = Services.GetRequiredService<MonitoringPollingService>();
        monitoringPollingService.SetPollingInterval(settingsService.Current.PollingIntervalMs);

        // Ensure ViewModels are subscribed before starting background polling.
        _ = Services.GetRequiredService<MonitoringViewModel>();
        _ = Services.GetRequiredService<ProcessesViewModel>();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        Current.MainWindow = mainWindow;

        if (settingsService.Current.StartMinimizedToTray)
        {
            mainWindow.PrepareStartMinimizedToTray();
            mainWindow.WindowState = WindowState.Minimized;
            mainWindow.ShowInTaskbar = false;
            mainWindow.ShowActivated = false;
            mainWindow.Show();
        }
        else
        {
            mainWindow.Show();
        }

        if (settingsService.Current.IsOverlayEnabled)
        {
            Services.GetRequiredService<OverlayService>().SetVisible(true);
        }

        StartSingleInstanceListener(mainWindow);

        monitoringPollingService.Start();
        Services.GetRequiredService<ProcessesPollingService>().Start();

        var vpnService = Services.GetRequiredService<VpnService>();
        _ = vpnService.InitializeAsync();
        if (settingsService.Current.VpnAutoStart)
        {
            _ = vpnService.ConnectAsync();
        }

        _ = Services.GetRequiredService<PlaytimeTrackerModule>().InitializeAsync();
        _ = Services.GetRequiredService<BatteryDeviceMonitorModule>().InitializeAsync();
        _ = Services.GetRequiredService<DiscordRpcModule>().InitializeAsync();
        _ = Services.GetRequiredService<NetworkRadarModule>().InitializeAsync();
        _ = Services.GetRequiredService<AutoGameModeService>().InitializeAsync();
        _ = Services.GetRequiredService<InputLayoutModule>().InitializeAsync();
        _ = Services.GetRequiredService<StutterMonitorModule>().InitializeAsync();
        _ = Services.GetRequiredService<RamAutoCleanSchedulerModule>().InitializeAsync();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => ResumeAllOnExit();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            FlushAndResumeOnCrash();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            FlushAndResumeOnCrash();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private void FlushAndResumeOnCrash()
    {
        ResumeAllOnExit();
        try
        {
            (Services.GetService(typeof(PlaytimeTrackerModule)) as PlaytimeTrackerModule)?.FlushSave();
            (Services.GetService(typeof(SettingsService)) as SettingsService)?.SaveSettings();
        }
        catch
        {
        }
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PulseGuard");
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "crash.log");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex?.ToString() ?? "Unknown error"}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
        }
    }

    private void ResumeAllOnExit()
    {
        try
        {
            (Services.GetService(typeof(ProcessMonitorModule)) as ProcessMonitorModule)?.ResumeAllSuspendedProcesses();
            (Services.GetService(typeof(OptimizationProfileService)) as OptimizationProfileService)?.ResumeAllProfileSuspendedProcesses();
        }
        catch
        {
        }
    }

    private void StartSingleInstanceListener(MainWindow mainWindow)
    {
        var showEvent = _showWindowEvent;
        var cts = _singleInstanceCts;
        if (showEvent is null || cts is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                WaitHandle[] handles = [showEvent, cts.Token.WaitHandle];
                while (!cts.IsCancellationRequested)
                {
                    int signaled = WaitHandle.WaitAny(handles);
                    if (signaled != 0 || cts.IsCancellationRequested)
                    {
                        break;
                    }

                    Dispatcher?.BeginInvoke(() => mainWindow.ViewModel.ShowWindowCommand.Execute(null));
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceCts?.Cancel();
        _singleInstanceCts?.Dispose();
        _singleInstanceCts = null;

        _showWindowEvent?.Dispose();
        _showWindowEvent = null;

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Ignore if mutex was not owned.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        ResumeAllOnExit();
        (Services.GetService(typeof(PlaytimeTrackerModule)) as PlaytimeTrackerModule)?.FlushSave();
        (Services.GetService(typeof(SettingsService)) as SettingsService)?.SaveSettings();
        (Services as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
