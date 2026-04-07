using System.Diagnostics;
using System.ServiceProcess;
using ZapretManager.Models;

namespace ZapretManager.Services;

public sealed class ZapretWindowsService : ServiceBase
{
    private readonly string _installationRootPath;
    private readonly string _profileToken;
    private ZapretServiceRuntime? _runtime;
    private bool _stopRequested;

    public ZapretWindowsService(string installationRootPath, string profileToken)
    {
        ServiceName = WindowsServiceManager.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
        _installationRootPath = installationRootPath;
        _profileToken = profileToken;
    }

    public static void RunFromArguments(string[] args)
    {
        if (args.Length < 3)
        {
            throw new InvalidOperationException("Не переданы параметры запуска службы Zapret.");
        }

        Run(new ZapretWindowsService(args[1], args[2]));
    }

    protected override void OnStart(string[] args)
    {
        try
        {
            _stopRequested = false;
            ZapretServiceLogger.Info($"SCM запустил службу zapret. Путь сборки: {_installationRootPath}. Профиль: {_profileToken}. Лог службы: {ZapretServiceLogger.CurrentLogPath}");
            _runtime = new ZapretServiceRuntime(_installationRootPath, _profileToken, RequestAdditionalTimeSafe);
            _runtime.UnexpectedExit += Runtime_UnexpectedExit;
            _runtime.Start();
            ZapretServiceLogger.Info("Служба zapret успешно перешла в рабочее состояние.");
        }
        catch (Exception ex)
        {
            ZapretServiceLogger.Error($"Служба zapret не смогла завершить старт: {ex.Message}");
            TryWriteEventLog($"Не удалось запустить службу zapret: {ex}");
            throw;
        }
    }

    private void RequestAdditionalTimeSafe(int milliseconds)
    {
        try
        {
            RequestAdditionalTime(milliseconds);
        }
        catch
        {
        }
    }

    protected override void OnStop()
    {
        _stopRequested = true;
        ZapretServiceLogger.Info("Получен запрос на остановку службы zapret.");
        StopRuntime();
    }

    protected override void OnShutdown()
    {
        _stopRequested = true;
        ZapretServiceLogger.Info("Получен системный shutdown для службы zapret.");
        StopRuntime();
        base.OnShutdown();
    }

    private void Runtime_UnexpectedExit(object? sender, EventArgs e)
    {
        if (_stopRequested)
        {
            return;
        }

        try
        {
            if (_runtime is not null && _runtime.TryRecoverAfterUnexpectedExit(out var recoveryMessage))
            {
                ZapretServiceLogger.Warning(recoveryMessage);
                TryWriteEventLog(recoveryMessage);
                return;
            }
        }
        catch (Exception ex)
        {
            ZapretServiceLogger.Error($"Повторный запуск winws.exe после неожиданного завершения не удался: {ex.Message}");
            TryWriteEventLog($"Повторный запуск winws.exe после неожиданного завершения не удался: {ex}");
        }

        var stopMessage = _runtime?.DescribeUnexpectedExit() ?? "Служба zapret остановлена: процесс winws.exe завершился неожиданно.";
        ZapretServiceLogger.Error(stopMessage);
        TryWriteEventLog(stopMessage);
        ExitCode = 1;

        try
        {
            Stop();
        }
        catch
        {
            try
            {
                Environment.Exit(1);
            }
            catch
            {
            }
        }
    }

    private void StopRuntime()
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            _runtime.UnexpectedExit -= Runtime_UnexpectedExit;
            _runtime.Dispose();
            ZapretServiceLogger.Info("Служба zapret остановлена.");
        }
        catch (Exception ex)
        {
            ZapretServiceLogger.Error($"Ошибка при остановке службы zapret: {ex.Message}");
            TryWriteEventLog($"Ошибка при остановке службы zapret: {ex}");
        }
        finally
        {
            _runtime = null;
        }
    }

    private static void TryWriteEventLog(string message)
    {
        try
        {
            EventLog.WriteEntry(WindowsServiceManager.ServiceName, message, EventLogEntryType.Error);
        }
        catch
        {
        }
    }

    private sealed class ZapretServiceRuntime : IDisposable
    {
        private readonly string _installationRootPath;
        private readonly string _profileToken;
        private readonly Action<int>? _requestAdditionalTime;
        private readonly ZapretDiscoveryService _discoveryService = new();
        private readonly object _sync = new();
        private static readonly TimeSpan[] StartupRecoveryDelays =
        [
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(35),
            TimeSpan.FromSeconds(55)
        ];
        private static readonly TimeSpan StartupRecoveryWindow = TimeSpan.FromMinutes(8);
        private ZapretInstallation? _installation;
        private ConfigProfile? _profile;
        private Process? _process;
        private bool _disposeRequested;
        private int _startupRecoveryAttempt;
        private int _launchAttempt;
        private int? _lastObservedExitCode;
        private DateTime? _lastObservedExitTime;

        public event EventHandler? UnexpectedExit;

        public ZapretServiceRuntime(string installationRootPath, string profileToken, Action<int>? requestAdditionalTime)
        {
            _installationRootPath = installationRootPath;
            _profileToken = profileToken;
            _requestAdditionalTime = requestAdditionalTime;
        }

        public void Start()
        {
            ZapretServiceLogger.Info($"Начало подготовки запуска winws. Время после загрузки Windows: {DescribeUptime()}.");
            WaitForStartupReadiness(_requestAdditionalTime);
            EnsureResolvedConfiguration();
            StartCore();
        }

        public bool TryRecoverAfterUnexpectedExit(out string recoveryMessage)
        {
            lock (_sync)
            {
                recoveryMessage = string.Empty;
                if (_disposeRequested)
                {
                    return false;
                }

                var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                if (uptime > StartupRecoveryWindow || _startupRecoveryAttempt >= StartupRecoveryDelays.Length)
                {
                    return false;
                }

                DisposeProcess();

                var delay = StartupRecoveryDelays[_startupRecoveryAttempt];
                _startupRecoveryAttempt++;
                recoveryMessage = $"winws.exe завершился слишком рано после загрузки Windows. Повторный запуск через {delay.TotalSeconds:0} сек. Попытка {_startupRecoveryAttempt} из {StartupRecoveryDelays.Length}.";

                SleepWithServiceHint(delay, _requestAdditionalTime);
                EnsureResolvedConfiguration();
                StartCore();
                return true;
            }
        }

        public void Dispose()
        {
            _disposeRequested = true;

            if (_process is null)
            {
                return;
            }

            try
            {
                if (!_process.HasExited)
                {
                    ZapretServiceLogger.Info($"Останавливаем winws.exe (PID {_process.Id}).");
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(10000);
                }
            }
            catch
            {
            }
            finally
            {
                DisposeProcess();
            }
        }

        private void Process_Exited(object? sender, EventArgs e)
        {
            CaptureProcessExitInfo();
            if (_disposeRequested)
            {
                return;
            }

            UnexpectedExit?.Invoke(this, EventArgs.Empty);
        }

        public string DescribeUnexpectedExit()
        {
            var baseMessage = "Служба zapret остановлена: процесс winws.exe завершился неожиданно.";
            if (_lastObservedExitCode.HasValue)
            {
                var timePart = _lastObservedExitTime.HasValue
                    ? $" Время: {_lastObservedExitTime.Value:HH:mm:ss}."
                    : string.Empty;
                return $"{baseMessage} Код выхода: {_lastObservedExitCode.Value}.{timePart} Попыток запуска в этой сессии: {_launchAttempt}.";
            }

            return $"{baseMessage} Попыток запуска в этой сессии: {_launchAttempt}.";
        }

        private ConfigProfile? ResolveProfile(ZapretInstallation installation)
        {
            return installation.Profiles.FirstOrDefault(item =>
                string.Equals(item.FileName, _profileToken, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, _profileToken, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.FilePath, _profileToken, StringComparison.OrdinalIgnoreCase));
        }

        private void EnsureResolvedConfiguration()
        {
            _installation ??= _discoveryService.TryLoad(_installationRootPath)
                ?? throw new InvalidOperationException($"Папка zapret не найдена: {_installationRootPath}");
            _profile ??= ResolveProfile(_installation)
                ?? throw new InvalidOperationException($"Не найден профиль для службы: {_profileToken}");
            ZapretServiceLogger.Info($"Для службы выбран профиль {_profile.FileName} из {_installation.RootPath}.");
        }

        private void StartCore()
        {
            if (_installation is null || _profile is null)
            {
                throw new InvalidOperationException("Служба zapret не подготовила конфигурацию перед стартом winws.exe.");
            }

            _launchAttempt++;
            _lastObservedExitCode = null;
            _lastObservedExitTime = null;
            var retryPart = _startupRecoveryAttempt > 0
                ? $", автоповтор {_startupRecoveryAttempt} из {StartupRecoveryDelays.Length}"
                : ", первый запуск после boot/старта службы";
            ZapretServiceLogger.Info($"Запускаем winws.exe. Попытка {_launchAttempt}{retryPart}. Профиль: {_profile.FileName}.");
            TryEnableTcpTimestamps();
            _process = ZapretBatchLauncher.StartAndAttachWinwsAsync(
                    _installation,
                    _profile,
                    TimeSpan.FromSeconds(12))
                .GetAwaiter()
                .GetResult();
            _process.EnableRaisingEvents = true;
            _process.Exited += Process_Exited;
            ZapretServiceLogger.Info($"winws.exe успешно запущен. PID {_process.Id}.");

            Thread.Sleep(450);
            if (_process.HasExited)
            {
                var exitCode = _process.ExitCode;
                _lastObservedExitCode = exitCode;
                _lastObservedExitTime = DateTime.Now;
                ZapretServiceLogger.Warning($"winws.exe завершился сразу после старта. Код выхода: {exitCode}. Попытка {_launchAttempt}.");
                DisposeProcess();
                throw new InvalidOperationException($"winws.exe завершился сразу после старта. Код выхода: {exitCode}.");
            }
        }

        private static void TryEnableTcpTimestamps()
        {
            try
            {
                WindowsServiceManager.RunHidden("netsh.exe", "interface tcp set global timestamps=enabled");
            }
            catch
            {
            }
        }

        private static void WaitForStartupReadiness(Action<int>? requestAdditionalTime)
        {
            try
            {
                var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                var minimumUptime = TimeSpan.FromSeconds(35);
                if (uptime < minimumUptime)
                {
                    ZapretServiceLogger.Info($"Служба ждёт стабилизацию Windows: uptime {uptime.TotalSeconds:0} сек, минимально нужно {minimumUptime.TotalSeconds:0} сек.");
                    SleepWithServiceHint(minimumUptime - uptime, requestAdditionalTime);
                }
            }
            catch
            {
            }

            ZapretServiceLogger.Info("Ожидаем готовность служб BFE и MpsSvc перед запуском winws.");
            WaitForServiceRunning("BFE", TimeSpan.FromSeconds(20), requestAdditionalTime);
            WaitForServiceRunning("MpsSvc", TimeSpan.FromSeconds(15), requestAdditionalTime);
            SleepWithServiceHint(TimeSpan.FromMilliseconds(1200), requestAdditionalTime);
        }

        private static void WaitForServiceRunning(string serviceName, TimeSpan timeout, Action<int>? requestAdditionalTime)
        {
            try
            {
                using var controller = new ServiceController(serviceName);
                var startedAtUtc = DateTime.UtcNow;

                while (DateTime.UtcNow - startedAtUtc < timeout)
                {
                    requestAdditionalTime?.Invoke(5000);
                    controller.Refresh();
                    if (controller.Status == ServiceControllerStatus.Running)
                    {
                        ZapretServiceLogger.Info($"Служба {serviceName} уже в состоянии RUNNING.");
                        return;
                    }

                    Thread.Sleep(400);
                }
            }
            catch
            {
            }
        }

        private static void SleepWithServiceHint(TimeSpan duration, Action<int>? requestAdditionalTime)
        {
            var remaining = duration;
            while (remaining > TimeSpan.Zero)
            {
                var slice = remaining > TimeSpan.FromSeconds(4)
                    ? TimeSpan.FromSeconds(4)
                    : remaining;

                requestAdditionalTime?.Invoke((int)Math.Max(slice.TotalMilliseconds + 1500, 2500));
                Thread.Sleep(slice);
                remaining -= slice;
            }
        }

        private void CaptureProcessExitInfo()
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                _lastObservedExitCode = _process.ExitCode;
                _lastObservedExitTime = DateTime.Now;
            }
            catch
            {
            }
        }

        private static string DescribeUptime()
        {
            try
            {
                var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                return $"{uptime.TotalSeconds:0} сек";
            }
            catch
            {
                return "неизвестно";
            }
        }

        private void DisposeProcess()
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                _process.Exited -= Process_Exited;
                _process.Dispose();
            }
            catch
            {
            }
            finally
            {
                _process = null;
            }
        }
    }
}
