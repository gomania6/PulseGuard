using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseGuard.Modules.Processes;
using PulseGuard.Modules.Processes.Models;

namespace PulseGuard.ViewModels;

public enum ProcessSortColumn
{
    Name,
    Cpu,
    Memory,
    Disk,
    Priority
}

public partial class ProcessesViewModel : ObservableObject, IDisposable
{
    private const int DefaultDisplayLimit = 40;

    private readonly ProcessMonitorModule _processModule;
    private readonly ProcessesPollingService _pollingService;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly Dictionary<string, ProcessItemViewModel> _itemsByName = new(128, StringComparer.OrdinalIgnoreCase);
    private readonly List<ProcessItemViewModel> _filteredBuffer = new(128);
    private bool _isPageActive;
    private int _totalGroupCount;
    private int _totalRawProcessCount;

    [ObservableProperty]
    private string _title = "Процессы";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _hideSystemProcesses = true;

    [ObservableProperty]
    private string _topCpuName = "—";

    [ObservableProperty]
    private string _topCpuValue = "0.0 %";

    [ObservableProperty]
    private string _topRamName = "—";

    [ObservableProperty]
    private string _topRamValue = "0.0 МБ";

    [ObservableProperty]
    private string _topDiskName = "—";

    [ObservableProperty]
    private string _topDiskValue = "0.00 МБ/с";

    [ObservableProperty]
    private string _processCountText = "Показано: 0 из 0";

    [ObservableProperty]
    private string _actionStatusText = "Защита системных процессов ProcessBlacklist активна";

    [ObservableProperty]
    private ProcessItemViewModel? _selectedProcess;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph))]
    [NotifyPropertyChangedFor(nameof(CpuSortGlyph))]
    [NotifyPropertyChangedFor(nameof(MemorySortGlyph))]
    [NotifyPropertyChangedFor(nameof(DiskSortGlyph))]
    [NotifyPropertyChangedFor(nameof(PrioritySortGlyph))]
    private ProcessSortColumn _sortColumn = ProcessSortColumn.Cpu;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph))]
    [NotifyPropertyChangedFor(nameof(CpuSortGlyph))]
    [NotifyPropertyChangedFor(nameof(MemorySortGlyph))]
    [NotifyPropertyChangedFor(nameof(DiskSortGlyph))]
    [NotifyPropertyChangedFor(nameof(PrioritySortGlyph))]
    private bool _sortDescending = true;

    public ObservableCollection<ProcessItemViewModel> VisibleProcesses { get; } = new();

    public string NameSortGlyph => GetSortGlyph(ProcessSortColumn.Name);
    public string CpuSortGlyph => GetSortGlyph(ProcessSortColumn.Cpu);
    public string MemorySortGlyph => GetSortGlyph(ProcessSortColumn.Memory);
    public string DiskSortGlyph => GetSortGlyph(ProcessSortColumn.Disk);
    public string PrioritySortGlyph => GetSortGlyph(ProcessSortColumn.Priority);

    public ProcessesViewModel(
        ProcessMonitorModule processModule,
        ProcessesPollingService pollingService,
        MainWindowViewModel mainWindowViewModel)
    {
        _processModule = processModule;
        _pollingService = pollingService;
        _mainWindowViewModel = mainWindowViewModel;

        _pollingService.SetMinimizedToTray(_mainWindowViewModel.IsMinimizedToTray);
        _mainWindowViewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;
        _pollingService.ProcessesUpdated += OnProcessesUpdated;
    }

    public void SetPageActive(bool isActive)
    {
        _isPageActive = isActive;
        _pollingService.SetPageActive(isActive);
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildVisibleList();
    }

    partial void OnHideSystemProcessesChanged(bool value)
    {
        RebuildVisibleList();
    }

    [RelayCommand]
    private void SortBy(string? columnName)
    {
        if (!Enum.TryParse<ProcessSortColumn>(columnName, out var targetColumn))
        {
            return;
        }

        if (SortColumn == targetColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = targetColumn;
            SortDescending = targetColumn != ProcessSortColumn.Name;
        }

        RebuildVisibleList();
    }

    [RelayCommand]
    private void TogglePriority(ProcessItemViewModel? item)
    {
        if (item is null || item.IsSystemProtected)
        {
            return;
        }

        if (_processModule.ToggleGroupPriority(item.Pids, item.Name, item.IsLowPriority, out var newPriority, out var message))
        {
            item.UpdateFrom(
                item.Pids,
                item.InstanceCount,
                item.CpuPercent,
                item.MemoryMb,
                item.DiskMbPerSec,
                item.BasePriority,
                newPriority);
            _pollingService.TriggerImmediatePoll();
        }

        ActionStatusText = message;
    }

    [RelayCommand]
    private void TrimWorkingSet(ProcessItemViewModel? item)
    {
        if (item is null || item.IsSystemProtected)
        {
            return;
        }

        if (_processModule.TrimGroupWorkingSet(item.Pids, item.Name, out var message))
        {
            _pollingService.TriggerImmediatePoll();
        }

        ActionStatusText = message;
    }

    private void OnMainWindowViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsMinimizedToTray))
        {
            _pollingService.SetMinimizedToTray(_mainWindowViewModel.IsMinimizedToTray);
        }
    }

    private void OnProcessesUpdated(ProcessesSnapshot snapshot)
    {
        if (!_isPageActive || _mainWindowViewModel.IsMinimizedToTray)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            if (_isPageActive && !_mainWindowViewModel.IsMinimizedToTray)
            {
                ApplySnapshot(snapshot);
            }
        });
    }

    private void ApplySnapshot(ProcessesSnapshot snapshot)
    {
        UpdateTopCards(snapshot);

        _totalGroupCount = snapshot.Processes.Count;
        _totalRawProcessCount = snapshot.TotalRawProcessCount;

        var activeNames = new HashSet<string>(snapshot.Processes.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var group in snapshot.Processes)
        {
            activeNames.Add(group.Name);

            if (_itemsByName.TryGetValue(group.Name, out var existing))
            {
                existing.UpdateFrom(
                    group.Pids,
                    group.InstanceCount,
                    group.CpuPercent,
                    group.MemoryMb,
                    group.DiskMbPerSec,
                    group.BasePriority,
                    group.PriorityClass);
            }
            else
            {
                var item = new ProcessItemViewModel(group.Name, group.IsSystemProtected, group.IsBackgroundSystemHost);
                item.UpdateFrom(
                    group.Pids,
                    group.InstanceCount,
                    group.CpuPercent,
                    group.MemoryMb,
                    group.DiskMbPerSec,
                    group.BasePriority,
                    group.PriorityClass);
                _itemsByName[group.Name] = item;
            }
        }

        // Remove exited process groups.
        List<string>? toRemove = null;
        foreach (var key in _itemsByName.Keys)
        {
            if (!activeNames.Contains(key))
            {
                toRemove ??= new List<string>();
                toRemove.Add(key);
            }
        }

        if (toRemove is not null)
        {
            foreach (var key in toRemove)
            {
                _itemsByName.Remove(key);
            }
        }

        RebuildVisibleList();
    }

    private void UpdateTopCards(ProcessesSnapshot snapshot)
    {
        if (snapshot.TopCpuProcess is { } topCpu)
        {
            TopCpuName = topCpu.InstanceCount > 1 ? $"{topCpu.Name} ({topCpu.InstanceCount})" : topCpu.Name;
            TopCpuValue = $"{topCpu.CpuPercent:F1} %";
        }
        else
        {
            TopCpuName = "—";
            TopCpuValue = "0.0 %";
        }

        if (snapshot.TopMemoryProcess is { } topRam)
        {
            TopRamName = topRam.InstanceCount > 1 ? $"{topRam.Name} ({topRam.InstanceCount})" : topRam.Name;
            TopRamValue = topRam.MemoryMb >= 1024.0
                ? $"{topRam.MemoryMb / 1024.0:F2} ГБ"
                : $"{topRam.MemoryMb:F1} МБ";
        }
        else
        {
            TopRamName = "—";
            TopRamValue = "0.0 МБ";
        }

        if (snapshot.TopDiskProcess is { } topDisk)
        {
            TopDiskName = topDisk.InstanceCount > 1 ? $"{topDisk.Name} ({topDisk.InstanceCount})" : topDisk.Name;
            TopDiskValue = $"{topDisk.DiskMbPerSec:F2} МБ/с";
        }
        else
        {
            TopDiskName = "—";
            TopDiskValue = "0.00 МБ/с";
        }
    }

    private void RebuildVisibleList()
    {
        _filteredBuffer.Clear();

        var hasSearch = !string.IsNullOrWhiteSpace(SearchText);
        var query = hasSearch ? SearchText.Trim() : string.Empty;

        foreach (var item in _itemsByName.Values)
        {
            if (HideSystemProcesses && (item.IsSystemProtected || item.IsBackgroundSystemHost))
            {
                continue;
            }

            if (hasSearch && !item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _filteredBuffer.Add(item);
        }

        _filteredBuffer.Sort(CompareItems);

        var totalMatched = _filteredBuffer.Count;
        var targetCount = hasSearch ? totalMatched : Math.Min(DefaultDisplayLimit, totalMatched);

        // Reconcile VisibleProcesses in-place so ListView virtualization keeps rows recycled.
        for (var i = VisibleProcesses.Count - 1; i >= targetCount; i--)
        {
            VisibleProcesses.RemoveAt(i);
        }

        for (var i = 0; i < targetCount; i++)
        {
            var desired = _filteredBuffer[i];
            if (i < VisibleProcesses.Count)
            {
                if (!ReferenceEquals(VisibleProcesses[i], desired))
                {
                    var existingIndex = VisibleProcesses.IndexOf(desired);
                    if (existingIndex >= 0)
                    {
                        VisibleProcesses.Move(existingIndex, i);
                    }
                    else
                    {
                        VisibleProcesses.Insert(i, desired);
                    }
                }
            }
            else
            {
                VisibleProcesses.Add(desired);
            }
        }

        while (VisibleProcesses.Count > targetCount)
        {
            VisibleProcesses.RemoveAt(VisibleProcesses.Count - 1);
        }

        ProcessCountText = hasSearch
            ? $"Найдено: {VisibleProcesses.Count} из {_totalGroupCount} групп ({_totalRawProcessCount} процессов)"
            : $"Топ-{VisibleProcesses.Count} из {_totalGroupCount} групп ({_totalRawProcessCount} процессов)";
    }

    private int CompareItems(ProcessItemViewModel a, ProcessItemViewModel b)
    {
        var cmp = SortColumn switch
        {
            ProcessSortColumn.Name => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            ProcessSortColumn.Cpu => a.CpuPercent.CompareTo(b.CpuPercent),
            ProcessSortColumn.Memory => a.MemoryMb.CompareTo(b.MemoryMb),
            ProcessSortColumn.Disk => a.DiskMbPerSec.CompareTo(b.DiskMbPerSec),
            ProcessSortColumn.Priority => a.BasePriority.CompareTo(b.BasePriority),
            _ => a.CpuPercent.CompareTo(b.CpuPercent)
        };

        if (SortDescending)
        {
            cmp = -cmp;
        }

        return cmp != 0
            ? cmp
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private string GetSortGlyph(ProcessSortColumn column)
    {
        if (SortColumn != column)
        {
            return string.Empty;
        }

        return SortDescending ? " ▼" : " ▲";
    }

    public void Dispose()
    {
        _mainWindowViewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
        _pollingService.ProcessesUpdated -= OnProcessesUpdated;
    }
}
