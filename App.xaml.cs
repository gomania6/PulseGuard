using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PulseGuard.Core.Interfaces;
using PulseGuard.Modules.Monitoring;
using PulseGuard.Modules.Optimization;
using PulseGuard.Modules.Overlay;
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

        // Settings
        services.AddSingleton<SettingsService>();
        services.AddSingleton<StartupTaskSchedulerService>();

        // Modules
        services.AddSingleton<SystemMonitorModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<SystemMonitorModule>());
        services.AddSingleton<MonitoringPollingService>();

        services.AddSingleton<ProcessMonitorModule>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<ProcessMonitorModule>());
        services.AddSingleton<ProcessesPollingService>();

        services.AddSingleton<SafeRamCleanerTool>();
        services.AddSingleton<IOptimizerTool>(sp => sp.GetRequiredService<SafeRamCleanerTool>());
        services.AddSingleton<TempFilesCleanerTool>();
        services.AddSingleton<IOptimizerTool>(sp => sp.GetRequiredService<TempFilesCleanerTool>());
        services.AddSingleton<GameModeTool>();
        services.AddSingleton<IOptimizerTool>(sp => sp.GetRequiredService<GameModeTool>());

        // Overlay
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<OverlayService>();

        // VPN
        services.AddSingleton<VpnService>();
        services.AddSingleton<IMonitorModule>(sp => sp.GetRequiredService<VpnService>());

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MonitoringViewModel>();
        services.AddSingleton<OptimizationViewModel>();
        services.AddSingleton<ProcessesViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // Views & Pages
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MonitoringPage>();
        services.AddSingleton<OptimizationPage>();
        services.AddSingleton<ProcessesPage>();
        services.AddSingleton<SettingsPage>();

        return services.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        var settingsService = Services.GetRequiredService<SettingsService>();
        var monitoringPollingService = Services.GetRequiredService<MonitoringPollingService>();
        monitoringPollingService.SetPollingInterval(settingsService.Current.PollingIntervalMs);

        // Ensure ViewModels are subscribed before starting background polling.
        _ = Services.GetRequiredService<MonitoringViewModel>();
        _ = Services.GetRequiredService<ProcessesViewModel>();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;

        if (settingsService.Current.IsOverlayEnabled)
        {
            Services.GetRequiredService<OverlayService>().SetVisible(true);
        }

        if (settingsService.Current.StartMinimizedToTray)
        {
            mainWindow.ShowInTaskbar = false;
            mainWindow.Opacity = 0;
            mainWindow.Show();
            mainWindow.ViewModel.MinimizeToTrayCommand.Execute(null);
            mainWindow.Opacity = 1;
            mainWindow.ShowInTaskbar = true;
        }
        else
        {
            mainWindow.Show();
        }

        monitoringPollingService.Start();
        Services.GetRequiredService<ProcessesPollingService>().Start();

        var vpnService = Services.GetRequiredService<VpnService>();
        _ = vpnService.InitializeAsync();
        if (settingsService.Current.VpnAutoStart)
        {
            _ = vpnService.ConnectAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (Services as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
