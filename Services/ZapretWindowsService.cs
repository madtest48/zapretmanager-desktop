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
            _runtime = new ZapretServiceRuntime(_installationRootPath, _profileToken);
            _runtime.UnexpectedExit += Runtime_UnexpectedExit;
            _runtime.Start();
        }
        catch (Exception ex)
        {
            TryWriteEventLog($"Не удалось запустить службу zapret: {ex}");
            throw;
        }
    }

    protected override void OnStop()
    {
        _stopRequested = true;
        StopRuntime();
    }

    protected override void OnShutdown()
    {
        _stopRequested = true;
        StopRuntime();
        base.OnShutdown();
    }

    private void Runtime_UnexpectedExit(object? sender, EventArgs e)
    {
        if (_stopRequested)
        {
            return;
        }

        TryWriteEventLog("Служба zapret остановлена: процесс winws.exe завершился неожиданно.");
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
        }
        catch (Exception ex)
        {
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
        private readonly ZapretDiscoveryService _discoveryService = new();
        private Process? _process;
        private bool _disposeRequested;

        public event EventHandler? UnexpectedExit;

        public ZapretServiceRuntime(string installationRootPath, string profileToken)
        {
            _installationRootPath = installationRootPath;
            _profileToken = profileToken;
        }

        public void Start()
        {
            var installation = _discoveryService.TryLoad(_installationRootPath)
                ?? throw new InvalidOperationException($"Папка zapret не найдена: {_installationRootPath}");
            var profile = ResolveProfile(installation)
                ?? throw new InvalidOperationException($"Не найден профиль для службы: {_profileToken}");

            TryEnableTcpTimestamps();
            _process = ZapretBatchLauncher.StartAndAttachWinwsAsync(
                    installation,
                    profile,
                    TimeSpan.FromSeconds(12))
                .GetAwaiter()
                .GetResult();
            _process.EnableRaisingEvents = true;
            _process.Exited += Process_Exited;

            Thread.Sleep(450);
            if (_process.HasExited)
            {
                var exitCode = _process.ExitCode;
                DisposeProcess();
                throw new InvalidOperationException($"winws.exe завершился сразу после старта. Код выхода: {exitCode}.");
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
            if (_disposeRequested)
            {
                return;
            }

            UnexpectedExit?.Invoke(this, EventArgs.Empty);
        }

        private ConfigProfile? ResolveProfile(ZapretInstallation installation)
        {
            return installation.Profiles.FirstOrDefault(item =>
                string.Equals(item.FileName, _profileToken, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, _profileToken, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.FilePath, _profileToken, StringComparison.OrdinalIgnoreCase));
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
