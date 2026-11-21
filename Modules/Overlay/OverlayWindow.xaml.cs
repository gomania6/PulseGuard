using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PulseGuard.Modules.Overlay;

/// <summary>
/// Interaction logic for OverlayWindow.xaml (no business logic).
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _altKeyTimer;
    private readonly DispatcherTimer _topmostTimer;
    private IntPtr _hwnd;
    private bool? _appliedClickThrough;

    public OverlayViewModel ViewModel { get; }

    public OverlayWindow(OverlayViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();

        _altKeyTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _altKeyTimer.Tick += (_, _) => UpdateClickThroughState();

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _topmostTimer.Tick += (_, _) => EnsureTopmost();

        SourceInitialized += OnSourceInitialized;
        Closed += OnWindowClosed;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyBaseOverlayStyles();
        UpdateClickThroughState();
        EnsureTopmost();
        _altKeyTimer.Start();
        _topmostTimer.Start();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _altKeyTimer.Stop();
        _topmostTimer.Stop();
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnWindowClosed;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void ApplyBaseOverlayStyles()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        int extendedStyle = OverlayNativeMethods.GetWindowLong(_hwnd, OverlayNativeMethods.GwlExStyle);
        extendedStyle |= OverlayNativeMethods.WsExToolWindow | OverlayNativeMethods.WsExNoActivate;
        OverlayNativeMethods.SetWindowLong(_hwnd, OverlayNativeMethods.GwlExStyle, extendedStyle);
    }

    private void EnsureTopmost()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        OverlayNativeMethods.SetWindowPos(
            _hwnd,
            OverlayNativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            OverlayNativeMethods.SwpNoMove | OverlayNativeMethods.SwpNoSize | OverlayNativeMethods.SwpNoActivate);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.IsClickThroughEnabled))
        {
            UpdateClickThroughState();
        }
    }

    private void UpdateClickThroughState()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        bool shouldBeClickThrough = ViewModel.IsClickThroughEnabled && !OverlayNativeMethods.IsAltKeyPressed();
        if (_appliedClickThrough == shouldBeClickThrough)
        {
            return;
        }

        int extendedStyle = OverlayNativeMethods.GetWindowLong(_hwnd, OverlayNativeMethods.GwlExStyle);
        if (shouldBeClickThrough)
        {
            extendedStyle |= OverlayNativeMethods.WsExTransparent;
        }
        else
        {
            extendedStyle &= ~OverlayNativeMethods.WsExTransparent;
        }

        OverlayNativeMethods.SetWindowLong(_hwnd, OverlayNativeMethods.GwlExStyle, extendedStyle);
        OverlayNativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            OverlayNativeMethods.SwpNoMove
            | OverlayNativeMethods.SwpNoSize
            | OverlayNativeMethods.SwpNoZOrder
            | OverlayNativeMethods.SwpNoActivate
            | OverlayNativeMethods.SwpFrameChanged);
        _appliedClickThrough = shouldBeClickThrough;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        bool canDrag = !ViewModel.IsPositionLocked || OverlayNativeMethods.IsAltKeyPressed();
        if (!canDrag
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
