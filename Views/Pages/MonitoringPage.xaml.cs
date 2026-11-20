using System.Windows.Controls;
using PulseGuard.ViewModels;

namespace PulseGuard.Views.Pages;

public partial class MonitoringPage : Page
{
    public MonitoringViewModel ViewModel { get; }

    public MonitoringPage(MonitoringViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}

