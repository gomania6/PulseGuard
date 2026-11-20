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
    public MainWindowViewModel ViewModel { get; }

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationViewPageProvider pageProvider,
        TrayNotificationService trayNotificationService)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        trayNotificationService.Attach(AppNotifyIcon);
        RootNavigation.SetPageProviderService(pageProvider);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(MonitoringPage));
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
    }

    private void NotifyIcon_OnLeftClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowWindowCommand.Execute(null);
    }
}
