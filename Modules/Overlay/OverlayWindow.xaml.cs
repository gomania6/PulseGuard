using System.Windows;
using System.Windows.Input;

namespace PulseGuard.Modules.Overlay;

/// <summary>
/// Interaction logic for OverlayWindow.xaml (no business logic).
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayViewModel ViewModel { get; }

    public OverlayWindow(OverlayViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (ViewModel.IsPositionLocked
            || e.ChangedButton != MouseButton.Left
            || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
            ViewModel.SavePosition(Left, Top);
        }
        catch (InvalidOperationException)
        {
            // Ignore if mouse button was released before DragMove started.
        }
    }
}
