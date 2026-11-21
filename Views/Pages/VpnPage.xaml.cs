using System.Windows.Controls;
using PulseGuard.ViewModels;

namespace PulseGuard.Views.Pages;

public partial class VpnPage : Page
{
    public VpnViewModel ViewModel { get; }

    public VpnPage(VpnViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}

