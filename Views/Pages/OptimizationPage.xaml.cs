using System.Windows.Controls;
using PulseGuard.ViewModels;

namespace PulseGuard.Views.Pages;

public partial class OptimizationPage : Page
{
    public OptimizationViewModel ViewModel { get; }

    public OptimizationPage(OptimizationViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}

