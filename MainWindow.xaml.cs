using System.ComponentModel;
using System.Windows;
using PulseGuard.Services;
using PulseGuard.ViewModels;
using PulseGuard.Views.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace PulseGuard;

/// <summary>
/// Interaction logic for MainWindow.xaml (no business logic).
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly GlobalHotkeyService _globalHotkeyService;
    private bool _isNavigationInitialized;
    private bool _hideOnFirstLoad;

    public MainWindowViewModel ViewModel { get; }

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationViewPageProvider pageProvider,
        TrayNotificationService trayNotificationService,
        GlobalHotkeyService globalHotkeyService)
    {
        ViewModel = viewModel;
        _globalHotkeyService = globalHotkeyService;
        DataContext = this;

        InitializeComponent();

        trayNotificationService.Attach(AppNotifyIcon);
        RootNavigation.SetPageProviderService(pageProvider);
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _globalHotkeyService.Attach(this);
    }

    public void PrepareStartMinimizedToTray()
    {
        _hideOnFirstLoad = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isNavigationInitialized)
        {
            _isNavigationInitialized = true;
            RootNavigation.Navigate(typeof(MonitoringPage));
        }

        if (_hideOnFirstLoad)
        {
            _hideOnFirstLoad = false;
            ViewModel.MinimizeToTrayCommand.Execute(null);
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized)
        {
            ViewModel.MinimizeToTrayCommand.Execute(null);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ViewModel.IsExplicitExitRequested && ViewModel.ShouldMinimizeToTrayOnClose)
        {
            e.Cancel = true;
            ViewModel.MinimizeToTrayCommand.Execute(null);
            return;
        }

        base.OnClosing(e);

        if (!ViewModel.IsExplicitExitRequested)
        {
            ViewModel.ExitApplicationCommand.Execute(null);
        }
    }

    private void NotifyIcon_OnLeftClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowWindowCommand.Execute(null);
    }
}
