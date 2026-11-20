using System.Windows.Controls;
using PulseGuard.ViewModels;

namespace PulseGuard.Views.Pages;

/// <summary>
/// Interaction logic for ProcessesPage.xaml (no business logic).
/// </summary>
public partial class ProcessesPage : Page
{
    public ProcessesViewModel ViewModel { get; }

    public ProcessesPage(ProcessesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();

        Loaded += (_, _) => ViewModel.SetPageActive(true);
        Unloaded += (_, _) => ViewModel.SetPageActive(false);
    }
}
