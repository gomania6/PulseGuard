using System.Windows.Controls;
using PulseGuard.ViewModels;

namespace PulseGuard.Views.Pages;

public partial class GameOptimizationPage : Page
{
    public GameOptimizationViewModel ViewModel { get; }

    public GameOptimizationPage(GameOptimizationViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}

