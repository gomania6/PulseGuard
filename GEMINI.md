# Project Rules & Architecture (System Monitor & Safe Optimizer)

## 1. Tech Stack & UI
- **Framework:** C# / .NET 8 (WPF), Windows 10/11 target.
- **UI Library:** Use `WPF-UI` (`Wpf.Ui` NuGet package) for modern Windows 11 Fluent Design, Dark Theme, NavigationView, and Mica/Acrylic backdrop.
- **Charts:** Use `LiveChartsCore.SkiaSharpView.WPF` for smooth, low-CPU real-time graphs.
- **Hardware Sensors:** Use `LibreHardwareMonitorLib` for CPU/GPU load, temperatures, and clocks. Use native `System.Diagnostics` / `NetworkInterface` for RAM and Network speed.

## 2. Strict Architecture (MVVM + Modular Plugins)
- Strictly follow **MVVM** (use `CommunityToolkit.Mvvm` with `[ObservableProperty]` and `[RelayCommand]`).
- **NO business logic in `.xaml.cs` code-behind.**
- **Modularity:** Every monitoring sensor or optimization tool must implement a common interface (`IMonitorModule` or `IOptimizerTool`) and live in its own folder under `/Modules/<FeatureName>/`.
- Adding a new feature must NOT require rewriting `MainWindow` logic — modules register via Dependency Injection (`Microsoft.Extensions.DependencyInjection`) in `App.xaml.cs`.

## 3. CRITICAL SAFETY RULES (System Protection)
- **Never kill processes (`Process.Kill()`)** automatically or during "Optimization".
- **Protected Blacklist:** Hardcode a static read-only HashSet of critical Windows processes (`System`, `Idle`, `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `svchost`, `dwm`, `explorer`, `smss`, `audiodg`). Any optimization action MUST skip these processes.
- **Safe RAM Optimization:** Only use Windows API `EmptyWorkingSet` / `SetProcessWorkingSetSize` on non-blacklisted user processes.
- **Safe Disk Cleanup:** Only target `%TEMP%`, Windows Temp (if admin), and Recycle Bin. Always wrap file deletion in `try-catch` to skip locked files silently. Show a preview of reclaimable MB before deleting.
- **No Registry Hacks:** Do NOT modify Windows Registry keys for "tweaks" or "speedups".

## 4. Performance & Resource Discipline
- Polling interval for hardware sensors must be configurable (default: `1000ms`), running on a background `PeriodicTimer` / `Task`, never blocking the UI thread.
- Minimize allocations in the polling loop (reuse objects/collections).
- Minimizing the window to System Tray should pause UI chart animations to drop CPU usage to ~0%.

## 5. Agent Workflow
- Before adding a new feature, read this file and check `/Docs/CHANGELOG.md`.
- After every completed task, update `/Docs/CHANGELOG.md` with created/modified files and brief notes.