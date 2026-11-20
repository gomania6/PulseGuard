using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Abstractions;

namespace PulseGuard.Services;

/// <summary>
/// Resolves NavigationView pages from the DI container.
/// </summary>
public sealed class PageService(IServiceProvider serviceProvider) : INavigationViewPageProvider
{
    public object? GetPage(Type pageType)
    {
        return serviceProvider.GetService(pageType);
    }
}

