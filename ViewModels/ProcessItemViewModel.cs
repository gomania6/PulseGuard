using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PulseGuard.ViewModels;

/// <summary>
/// Row ViewModel representing a grouped application in the Processes ListView.
/// Updated in-place on each polling tick to avoid UI flickering and allocations.
/// </summary>
public partial class ProcessItemViewModel : ObservableObject
{
    public string Name { get; }
    public bool IsSystemProtected { get; }
    public bool IsBackgroundSystemHost { get; }

    [ObservableProperty]
    private IReadOnlyList<int> _pids = Array.Empty<int>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private int _instanceCount = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuText))]
    private double _cpuPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryText))]
    private double _memoryMb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskText))]
    private double _diskMbPerSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLowPriority))]
    [NotifyPropertyChangedFor(nameof(PriorityActionTooltip))]
    private ProcessPriorityClass? _priorityClass;

    [ObservableProperty]
    private int _basePriority;

    [ObservableProperty]
    private string _priorityText = "Обычный";

    public ProcessItemViewModel(string name, bool isSystemProtected, bool isBackgroundSystemHost)
    {
        Name = name;
        IsSystemProtected = isSystemProtected;
        IsBackgroundSystemHost = isBackgroundSystemHost;
    }

    public string DisplayName => InstanceCount > 1 ? $"{Name} ({InstanceCount})" : Name;

    public bool CanModify => !IsSystemProtected;

    public bool IsLowPriority => PriorityClass is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle;

    public string CpuText => $"{CpuPercent:F1} %";

    public string MemoryText => $"{MemoryMb:F1} МБ";

    public string DiskText => $"{DiskMbPerSec:F2} МБ/с";

    public string PriorityActionTooltip => IsSystemProtected
        ? "Защищённый системный процесс Windows"
        : IsLowPriority
            ? "Вернуть обычный приоритет (Normal)"
            : "Понизить приоритет (BelowNormal)";

    public string TrimRamTooltip => IsSystemProtected
        ? "Защищённый системный процесс Windows"
        : "Освободить ОЗУ (EmptyWorkingSet)";

    public void UpdateFrom(
        IReadOnlyList<int> pids,
        int instanceCount,
        double cpuPercent,
        double memoryMb,
        double diskMbPerSec,
        int basePriority,
        ProcessPriorityClass? priorityClass)
    {
        Pids = pids;
        InstanceCount = instanceCount;
        CpuPercent = cpuPercent;
        MemoryMb = memoryMb;
        DiskMbPerSec = diskMbPerSec;
        BasePriority = basePriority;
        PriorityClass = priorityClass;
        PriorityText = FormatPriority(priorityClass, basePriority);
    }

    private static string FormatPriority(ProcessPriorityClass? priorityClass, int basePriority)
    {
        return priorityClass switch
        {
            ProcessPriorityClass.Idle => "Низкий",
            ProcessPriorityClass.BelowNormal => "Ниже среднего",
            ProcessPriorityClass.Normal => "Обычный",
            ProcessPriorityClass.AboveNormal => "Выше среднего",
            ProcessPriorityClass.High => "Высокий",
            ProcessPriorityClass.RealTime => "Реального времени",
            _ => basePriority > 0 ? $"Базовый ({basePriority})" : "Системный"
        };
    }
}
