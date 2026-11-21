# PulseGuard

**PulseGuard** — модульное десктопное приложение для Windows 10/11 (`.NET 8`, `WPF`), объединяющее мониторинг аппаратных датчиков в реальном времени, игровой оверлей, сетевой радар лагов, контроль микрофризов и безопасную оптимизацию системы с защитой критических процессов и античитов.

---

## Основные возможности

### Системный мониторинг и стабильность
- **CPU, GPU, ОЗУ и Сеть:** загрузка, частоты, температура, VRAM, скорость загрузки/отдачи и графики в реальном времени (`LiveChartsCore`).
- **Надёжный 5-уровневый каскад температуры CPU:**
  1. `LibreHardwareMonitorLib` (`HardwareType.Cpu` и `HardwareType.Motherboard` / SuperIO)
  2. `HWiNFO64` Shared Memory (`Global\HWiNFO_SENS_SM2`)
  3. WMI (`root\LibreHardwareMonitor` и `root\OpenHardwareMonitor`)
  4. WMI ACPI (`root\WMI` → `MSAcpi_ThermalZoneTemperature`)
  5. PDH `Thermal Zone Information` (`High Precision Temperature` и `Temperature`)
  - В настройках доступен **режим совместимости** (отключение kernel-драйвера CPU) для систем с жёсткими ограничениями HVCI / Memory Integrity или античитами.
- **Стабильность / Микрофризы:** отслеживание отклонений таймера, `% DPC Time`, очереди CPU (`Processor Queue Length`) и выявление фоновых процессов, провоцирующих всплески нагрузки.
- **Минимальная нагрузка в фоне:** настраиваемый интервал опроса (`500–5000 мс`) и автоматическая пауза графиков/опроса при сворачивании в системный трей.

### Сеть и VPN
- **Сетевой радар лагов (ETW):** определение Топ-3 процессов-потребителей трафика в реальном времени с возможностью безопасной заморозки (`NtSuspendProcess`) и гарантированной разморозкой при выходе.
- **Мониторинг и управление VPN:** состояние туннеля, внешний IP, ping, jitter, процент потерь пакетов (`Packet Loss`) и автоматический выбор ближайшего проверочного сервера.
- **Оптимизация DNS и сети:** тестирование популярных DNS-резолверов (`1.1.1.1`, `8.8.8.8`, `9.9.9.9`, `77.88.8.8`), переключение на самый быстрый и сброс DNS-кэша.

### Игровой оверлей (Overlay)
- Компактный мини-виджет поверх игр (включая режим *Borderless Fullscreen*) с прозрачностью для кликов (`WS_EX_TRANSPARENT`, перетаскивание при удержании `Alt`).
- Отображение CPU/GPU/RAM/сети, температуры, статуса VPN, текущей раскладки клавиатуры (`RU` / `EN`), заряда беспроводных HID-устройств, уведомлений о микрофризах и таймера игровой сессии.
- **Интеграция с Discord RPC:** отображение участников голосового канала и индикаторов активности (микрофон, звук, стрим, камера).

### Игровой режим, оптимизация и трекер времени
- **Безопасная очистка ОЗУ и кэша:** освобождение рабочего набора памяти (`EmptyWorkingSet` / `SetProcessWorkingSetSize`) и очистка `%TEMP%`, `Windows\Temp`, `D3DSCache` и Корзины.
- **Авто-игровой режим и профили:** автоматическое определение запущенных игр, настройка схемы электропитания и приоритетов, защита античитов (`vgc`, `BEService`, `EasyAntiCheat`), резервное копирование и полный откат параметров (`registry_backup.json`).
- **Трекер игрового времени:** статистика по играм за сегодня, вчера, 7 дней и всё время с интерактивным календарём активности.
- **Глобальные горячие клавиши:** `Alt+F1` (оверлей), `Alt+F2` (быстрая очистка RAM), `Alt+F3` (VPN), `Alt+F5` (Игровой режим), `Alt+Home` / настраиваемый хоткей для показа и скрытия главного окна.

---

## Стек и архитектура

- **Платформа:** C# / `.NET 8` (`net8.0-windows`, `win-x64`), WPF
- **UI:** [WPF-UI](https://github.com/lepoco/wpfui) (Fluent Design, тёмная тема, `NavigationView`)
- **Графики:** `LiveChartsCore.SkiaSharpView.WPF`
- **Архитектура:** строгий **MVVM** (`CommunityToolkit.Mvvm`) + модульные плагины (`IMonitorModule` / `IOptimizerTool`), регистрируемые через `Microsoft.Extensions.DependencyInjection` в `App.xaml.cs`
- **Датчики и системные API:** `LibreHardwareMonitorLib`, `HidSharp`, `Microsoft.Diagnostics.Tracing.TraceEvent`, нативные Win32/PDH/WMI вызовы

---

## Безопасность

- Критические системные процессы (`System`, `Idle`, `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `svchost`, `dwm`, `explorer`, `smss`, `audiodg`) и процессы античитов/VPN строго защищены от завершения, заморозки и изменения приоритета.
- Все операции с файлами кэша и временными файлами выполняются с предварительным расчётом объёма и пропуском заблокированных файлов.
- При аварийном завершении работы предусмотрено автоматическое восстановление замороженных процессов и сохранение статистики (`%AppData%\PulseGuard\crash.log`).

---

## Сборка и запуск

### Требования
- Windows 10 / 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Сборка (Debug / Release)
```powershell
dotnet build monitor/monitor.csproj -c Release
```

### Публикация единого исполняемого файла (`PublishSingleFile`)
```powershell
dotnet publish monitor/monitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./Publish
```
Готовый файл появится в `./Publish/PulseGuard.exe`.

---

## Структура проекта

```text
monitor/
├── Docs/
│   └── CHANGELOG.md              # История изменений проекта
├── monitor/
│   ├── Core/                     # Интерфейсы модулей, безопасность (ProcessBlacklist)
│   ├── Modules/
│   │   ├── BatteryDevices/       # Заряд беспроводных HID-устройств
│   │   ├── Discord/              # Интеграция с Discord RPC
│   │   ├── InputLayout/          # Определение активной раскладки клавиатуры
│   │   ├── Monitoring/           # SystemMonitorModule, StutterMonitorModule, SafeCpuTemperatureFallback
│   │   ├── Network/              # Сетевой радар лагов (ETW) и оптимизатор DNS/TCP
│   │   ├── Optimization/         # Игровой режим, очистка RAM/Temp, профили, авто-очистка
│   │   ├── Overlay/              # Игровой оверлей поверх окон
│   │   ├── Playtime/             # Учёт игрового времени и история по дням
│   │   ├── Processes/            # Управление процессами (безопасный suspend/resume)
│   │   ├── Settings/             # Настройки и автозапуск
│   │   └── Vpn/                  # Мониторинг и управление VPN
│   ├── Services/                 # Уведомления в трее, глобальные горячие клавиши
│   ├── ViewModels/               # ViewModel для всех вкладок
│   └── Views/                    # XAML-страницы интерфейса
├── GEMINI.md                     # Правила архитектуры и безопасности
└── monitor.slnx                  # Файл решения
```

---

## Диагностика

При работе приложение сохраняет настройки и диагностические файлы в `%AppData%\PulseGuard\`:
- `settings.json` — пользовательские настройки
- `cpu_sensor_diag.txt` — отчёт проверки всех 5 источников температуры CPU при запуске
- `playtime_stats.json` / `playtime_stats.json.bak` — статистика игрового времени
- `crash.log` — журнал необработанных исключений (при их возникновении)

