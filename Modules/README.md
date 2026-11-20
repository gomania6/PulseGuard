# Modules

Place each monitoring sensor (`IMonitorModule`) or safe optimization tool (`IOptimizerTool`) in its own subfolder:

- `/Modules/<FeatureName>/`

Register new modules in `App.xaml.cs` via `Microsoft.Extensions.DependencyInjection`.

