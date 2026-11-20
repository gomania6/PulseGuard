using System.Windows;
using PulseGuard.Modules.Settings;
using PulseGuard.Modules.Settings.Models;

namespace PulseGuard.Modules.Overlay;

/// <summary>
/// Manages lifecycle, corner/custom positioning, and visibility state of <see cref="OverlayWindow"/>.
/// </summary>
public sealed class OverlayService : IDisposable
{
    private const double ScreenEdgeMargin = 18.0;

    private readonly OverlayViewModel _viewModel;
    private readonly SettingsService _settingsService;

    private OverlayWindow? _window;
    private bool _disposed;

    public bool IsVisible => _window is not null && _window.IsVisible;

    public event Action<bool>? VisibilityChanged;

    public OverlayService(OverlayViewModel viewModel, SettingsService settingsService)
    {
        _viewModel = viewModel;
        _settingsService = settingsService;
        _viewModel.CloseRequested += OnViewModelCloseRequested;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Toggle() => SetVisible(!IsVisible);

    public void SetVisible(bool visible)
    {
        if (_disposed)
        {
            return;
        }

        if (visible == IsVisible)
        {
            return;
        }

        _settingsService.Update(s => s.IsOverlayEnabled = visible);

        if (visible)
        {
            ShowOverlay();
        }
        else
        {
            HideOverlay();
        }

        VisibilityChanged?.Invoke(IsVisible);
    }

    private void ShowOverlay()
    {
        if (_window is not null)
        {
            _viewModel.SetActive(true);
            _window.Show();
            ApplyPosition(_window);
            return;
        }

        _viewModel.SetActive(true);

        var window = new OverlayWindow(_viewModel);
        window.Loaded += (_, _) => ApplyPosition(window);
        window.SizeChanged += (_, _) =>
        {
            if (_settingsService.Current.OverlayPosition != OverlayCorner.Custom)
            {
                ApplyPosition(window);
            }
        };
        _window = window;

        var settings = _settingsService.Current;
        if (settings.OverlayPosition == OverlayCorner.Custom
            && settings.OverlayLeft.HasValue
            && settings.OverlayTop.HasValue)
        {
            window.Left = settings.OverlayLeft.Value;
            window.Top = settings.OverlayTop.Value;
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            window.Left = workArea.Right - 480;
            window.Top = workArea.Top + ScreenEdgeMargin;
        }

        window.Show();
    }

    private void HideOverlay()
    {
        _viewModel.SetActive(false);

        if (_window is null)
        {
            return;
        }

        _window.Close();
        _window = null;
    }

    private void OnSettingsChanged(AppSettings _)
    {
        var window = _window;
        if (window is null || !window.IsVisible)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_window is not null && _window.IsVisible)
            {
                ApplyPosition(_window);
            }
        });
    }

    private void ApplyPosition(OverlayWindow window)
    {
        double width = window.ActualWidth > 0 ? window.ActualWidth : 460;
        double height = window.ActualHeight > 0 ? window.ActualHeight : 36;

        var settings = _settingsService.Current;
        var workArea = SystemParameters.WorkArea;

        double desiredLeft;
        double desiredTop;

        switch (settings.OverlayPosition)
        {
            case OverlayCorner.TopLeft:
                desiredLeft = workArea.Left + ScreenEdgeMargin;
                desiredTop = workArea.Top + ScreenEdgeMargin;
                break;

            case OverlayCorner.TopRight:
                desiredLeft = workArea.Right - width - ScreenEdgeMargin;
                desiredTop = workArea.Top + ScreenEdgeMargin;
                break;

            case OverlayCorner.BottomLeft:
                desiredLeft = workArea.Left + ScreenEdgeMargin;
                desiredTop = workArea.Bottom - height - ScreenEdgeMargin;
                break;

            case OverlayCorner.BottomRight:
                desiredLeft = workArea.Right - width - ScreenEdgeMargin;
                desiredTop = workArea.Bottom - height - ScreenEdgeMargin;
                break;

            case OverlayCorner.Custom:
                if (settings.OverlayLeft.HasValue && settings.OverlayTop.HasValue)
                {
                    desiredLeft = settings.OverlayLeft.Value;
                    desiredTop = settings.OverlayTop.Value;
                }
                else
                {
                    desiredLeft = workArea.Right - width - ScreenEdgeMargin;
                    desiredTop = workArea.Top + ScreenEdgeMargin;
                }
                break;

            default:
                desiredLeft = workArea.Right - width - ScreenEdgeMargin;
                desiredTop = workArea.Top + ScreenEdgeMargin;
                break;
        }

        double minLeft = SystemParameters.VirtualScreenLeft;
        double minTop = SystemParameters.VirtualScreenTop;
        double maxLeft = Math.Max(minLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - width);
        double maxTop = Math.Max(minTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height);

        window.Left = Math.Clamp(desiredLeft, minLeft, maxLeft);
        window.Top = Math.Clamp(desiredTop, minTop, maxTop);
    }

    private void OnViewModelCloseRequested()
    {
        SetVisible(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.CloseRequested -= OnViewModelCloseRequested;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        HideOverlay();
        _viewModel.Dispose();
    }
}
