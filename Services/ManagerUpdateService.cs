using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using ZapretManager.Models;

namespace ZapretManager.Services;

public sealed class ManagerUpdateService
{
    private const string LatestReleaseUrl = "https://github.com/Valturere/zapretmanager-desktop/releases/latest";
    private const string ReleaseBaseUrl = "https://github.com/Valturere/zapretmanager-desktop/releases/tag/";
    private const string ExpandedAssetsUrl = "https://github.com/Valturere/zapretmanager-desktop/releases/expanded_assets/";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<ManagerUpdateInfo> GetUpdateInfoAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await HttpClient.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var tagName = ExtractTagName(response);
            if (string.IsNullOrWhiteSpace(tagName))
            {
                throw new InvalidOperationException("GitHub вернул релиз без тега версии.");
            }

            var latestVersion = NormalizeVersion(tagName);
            var releaseUrl = ReleaseBaseUrl + tagName;

            var currentComparable = ParseComparableVersion(currentVersion);
            var latestComparable = ParseComparableVersion(latestVersion);
            var isUpdateAvailable = currentComparable.CompareTo(latestComparable) < 0;

            string? downloadUrl = null;
            string? assetFileName = null;

            if (isUpdateAvailable)
            {
                var html = await HttpClient.GetStringAsync(ExpandedAssetsUrl + tagName, cancellationToken);
                var match = Regex.Match(
                    html,
                    "href=\"(?<href>/Valturere/zapretmanager-desktop/releases/download/[^\"]+\\.exe)\"",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    var href = match.Groups["href"].Value;
                    downloadUrl = "https://github.com" + href;
                    assetFileName = Path.GetFileName(Uri.UnescapeDataString(href));
                }
            }

            if (isUpdateAvailable && string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new InvalidOperationException("На GitHub найдена новая версия программы, но в релизе нет exe-файла для скачивания.");
            }

            return new ManagerUpdateInfo
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                DownloadUrl = downloadUrl,
                AssetFileName = assetFileName,
                ReleasePageUrl = releaseUrl,
                IsUpdateAvailable = isUpdateAvailable
            };
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (NetworkErrorTranslator.IsNetworkException(ex))
        {
            throw NetworkErrorTranslator.CreateGitHubException(ex, "Проверка обновления программы");
        }
    }

    public async Task<string> DownloadUpdateAsync(
        string downloadUrl,
        string? assetFileName,
        string latestVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException("Не указана ссылка для скачивания новой версии программы.");
        }

        try
        {
            var updateDirectory = GetUpdateDirectory();
            Directory.CreateDirectory(updateDirectory);
            CleanupOldFiles(updateDirectory);

            var safeFileName = string.IsNullOrWhiteSpace(assetFileName)
                ? $"ZapretManager-{latestVersion}.exe"
                : assetFileName;
            var targetPath = Path.Combine(updateDirectory, safeFileName);

            await using var sourceStream = await HttpClient.GetStreamAsync(downloadUrl, cancellationToken);
            await using var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await sourceStream.CopyToAsync(targetStream, cancellationToken);
            await targetStream.FlushAsync(cancellationToken);

            return targetPath;
        }
        catch (Exception ex) when (NetworkErrorTranslator.IsNetworkException(ex))
        {
            throw NetworkErrorTranslator.CreateGitHubException(ex, "Скачивание обновления программы");
        }
    }

    public async Task LaunchPreparedUpdateAsync(
        string downloadedExecutablePath,
        string currentExecutablePath,
        int currentProcessId,
        string? hostedServiceName = null,
        bool restartHostedServiceDuringUpdate = false,
        bool reinstallHostedServiceAfterUpdate = false,
        string? hostedServiceInstallationRootPath = null,
        string? hostedServiceProfileToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadedExecutablePath) || !File.Exists(downloadedExecutablePath))
        {
            throw new InvalidOperationException("Не найден скачанный файл новой версии программы.");
        }

        if (string.IsNullOrWhiteSpace(currentExecutablePath) || !File.Exists(currentExecutablePath))
        {
            throw new InvalidOperationException("Не удалось определить текущий файл ZapretManager.");
        }

        var updateDirectory = GetUpdateDirectory();
        Directory.CreateDirectory(updateDirectory);

        var backupPath = currentExecutablePath + ".old";
        var logPath = Path.Combine(updateDirectory, "manager-update.log");
        var scriptPath = Path.Combine(updateDirectory, $"apply-manager-update-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            BuildUpdateScriptContent(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            cancellationToken);

        var requiresElevation = !CanWriteToDirectory(Path.GetDirectoryName(currentExecutablePath)!);
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments =
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File {QuoteArgument(scriptPath)} -CurrentProcessId {currentProcessId} -SourcePath {QuoteArgument(downloadedExecutablePath)} -TargetPath {QuoteArgument(currentExecutablePath)} -BackupPath {QuoteArgument(backupPath)} -LogPath {QuoteArgument(logPath)} -HostedServiceName {QuoteArgument(hostedServiceName ?? string.Empty)} -RestartHostedServiceFlag {(restartHostedServiceDuringUpdate ? 1 : 0)} -ReinstallHostedServiceFlag {(reinstallHostedServiceAfterUpdate ? 1 : 0)} -HostedServiceInstallationRootPath {QuoteArgument(hostedServiceInstallationRootPath ?? string.Empty)} -HostedServiceProfileToken {QuoteArgument(hostedServiceProfileToken ?? string.Empty)}",
            WorkingDirectory = updateDirectory,
            UseShellExecute = true
        };

        if (requiresElevation)
        {
            startInfo.Verb = "runas";
        }

        try
        {
            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить установщик обновления программы.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Обновление программы отменено.", ex);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretManager");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private static string? ExtractTagName(HttpResponseMessage response)
    {
        var requestUri = response.RequestMessage?.RequestUri?.AbsoluteUri;
        if (!string.IsNullOrWhiteSpace(requestUri))
        {
            var match = Regex.Match(
                requestUri,
                "/releases/tag/(?<tag>[^/?#]+)",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups["tag"].Value;
            }
        }

        if (response.Headers.Location is not null)
        {
            var location = response.Headers.Location.ToString();
            var match = Regex.Match(
                location,
                "/releases/tag/(?<tag>[^/?#]+)",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups["tag"].Value;
            }
        }

        return null;
    }

    private static string GetUpdateDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZapretManager",
            "ManagerUpdate");
    }

    private static void CleanupOldFiles(string updateDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(updateDirectory, "*.exe"))
        {
            TryDeleteFile(file);
        }

        foreach (var file in Directory.EnumerateFiles(updateDirectory, "*.ps1"))
        {
            TryDeleteFile(file);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static string NormalizeVersion(string version)
    {
        var value = version.Trim();
        return value.StartsWith('v') || value.StartsWith('V')
            ? value[1..]
            : value;
    }

    private static ComparableVersion ParseComparableVersion(string version)
    {
        var normalized = NormalizeVersion(version);
        var parts = normalized.Split('-', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var numericParts = parts[0]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static item => int.TryParse(item, out var parsed) ? parsed : 0)
            .ToArray();

        var major = numericParts.Length > 0 ? numericParts[0] : 0;
        var minor = numericParts.Length > 1 ? numericParts[1] : 0;
        var patch = numericParts.Length > 2 ? numericParts[2] : 0;
        var isPrerelease = parts.Length > 1;
        var prereleaseNumber = 0;

        if (isPrerelease)
        {
            var suffix = parts[1];
            if (suffix.StartsWith("dev", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(suffix[3..], out prereleaseNumber);
            }
        }

        return new ComparableVersion(major, minor, patch, isPrerelease, prereleaseNumber);
    }

    private static bool CanWriteToDirectory(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            var testPath = Path.Combine(directoryPath, $".zapretmanager-write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(testPath, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteArgument(string argument)
    {
        return $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string BuildUpdateScriptContent()
    {
        return """
param(
    [int]$CurrentProcessId,
    [string]$SourcePath,
    [string]$TargetPath,
    [string]$BackupPath,
    [string]$LogPath = '',
    [string]$HostedServiceName = '',
    [int]$RestartHostedServiceFlag = 0,
    [int]$ReinstallHostedServiceFlag = 0,
    [string]$HostedServiceInstallationRootPath = '',
    [string]$HostedServiceProfileToken = ''
)

$ErrorActionPreference = 'Stop'
$restartHostedService = $RestartHostedServiceFlag -ne 0
$reinstallHostedService = $ReinstallHostedServiceFlag -ne 0

function Write-UpdateLog {
    param([string]$Message)

    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        return
    }

    try {
        $directory = Split-Path -Path $LogPath -Parent
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }

        Add-Content -LiteralPath $LogPath -Encoding UTF8 -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Message)
    }
    catch {
    }
}

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    try {
        Set-Content -LiteralPath $LogPath -Encoding UTF8 -Value ("[{0}] Старт обновления программы." -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'))
    }
    catch {
    }
}

for ($i = 0; $i -lt 120; $i++) {
    try {
        Get-Process -Id $CurrentProcessId -ErrorAction Stop | Out-Null
        Start-Sleep -Milliseconds 500
    }
    catch {
        break
    }
}

Start-Sleep -Milliseconds 350

$backupCreated = $false
$hostedServiceStopped = $false
$reinstallHostedServicePrepared = $reinstallHostedService -and
    -not [string]::IsNullOrWhiteSpace($HostedServiceName) -and
    -not [string]::IsNullOrWhiteSpace($HostedServiceInstallationRootPath) -and
    -not [string]::IsNullOrWhiteSpace($HostedServiceProfileToken)
Write-UpdateLog "Подготовка завершена. restartHostedService=$restartHostedService; reinstallHostedService=$reinstallHostedServicePrepared."

if ($restartHostedService -and -not [string]::IsNullOrWhiteSpace($HostedServiceName)) {
    try {
        Write-UpdateLog "Останавливаем службу $HostedServiceName перед заменой exe."
        $service = Get-Service -Name $HostedServiceName -ErrorAction Stop
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $HostedServiceName -Force -ErrorAction Stop
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
            $hostedServiceStopped = $true
        }

        Write-UpdateLog "Служба $HostedServiceName остановлена."
    }
    catch {
        Write-UpdateLog "Ошибка остановки службы: $($_.Exception.Message)"
        throw "Не удалось остановить службу перед обновлением программы: $($_.Exception.Message)"
    }
}

try {
    Write-UpdateLog "Начинаем замену файла программы."
    if (Test-Path -LiteralPath $BackupPath) {
        Remove-Item -LiteralPath $BackupPath -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $TargetPath) {
        Move-Item -LiteralPath $TargetPath -Destination $BackupPath -Force
        $backupCreated = $true
        Write-UpdateLog "Текущий exe перемещён во временную копию."
    }

    Move-Item -LiteralPath $SourcePath -Destination $TargetPath -Force
    Write-UpdateLog "Новый exe установлен на место."

    if ($reinstallHostedServicePrepared) {
        $quotedInstallationRootPath = '"' + $HostedServiceInstallationRootPath.Replace('"', '\"') + '"'
        $quotedProfileToken = '"' + $HostedServiceProfileToken.Replace('"', '\"') + '"'
        $installArgs = "--install-service $quotedInstallationRootPath $quotedProfileToken"

        Write-UpdateLog "Переустанавливаем службу на новый exe."
        $installProcess = Start-Process -FilePath $TargetPath -ArgumentList $installArgs -Wait -PassThru -WindowStyle Hidden
        if ($installProcess.ExitCode -ne 0) {
            Write-UpdateLog "Переустановка службы завершилась с ошибкой: код $($installProcess.ExitCode)."
            throw "Автоматическая переустановка службы после обновления программы завершилась с кодом $($installProcess.ExitCode)."
        }

        Write-UpdateLog "Переустановка службы завершена успешно."
    }
    elseif ($hostedServiceStopped -and -not [string]::IsNullOrWhiteSpace($HostedServiceName)) {
        Write-UpdateLog "Запускаем ранее остановленную службу $HostedServiceName."
        Start-Service -Name $HostedServiceName -ErrorAction Stop
        Write-UpdateLog "Служба $HostedServiceName снова запущена."
    }

    Write-UpdateLog "Запускаем обновлённый ZapretManager."
    Start-Process -FilePath $TargetPath

    Start-Sleep -Seconds 2

    if ($backupCreated -and (Test-Path -LiteralPath $BackupPath)) {
        Remove-Item -LiteralPath $BackupPath -Force -ErrorAction SilentlyContinue
    }

    Write-UpdateLog "Обновление завершено успешно."
}
catch {
    Write-UpdateLog "Ошибка обновления: $($_.Exception.Message)"
    if ($hostedServiceStopped -and -not [string]::IsNullOrWhiteSpace($HostedServiceName)) {
        try {
            Start-Service -Name $HostedServiceName -ErrorAction SilentlyContinue
        }
        catch {
        }
    }

    if ($backupCreated -and (Test-Path -LiteralPath $BackupPath) -and -not (Test-Path -LiteralPath $TargetPath)) {
        Move-Item -LiteralPath $BackupPath -Destination $TargetPath -Force
    }

    throw
}
finally {
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
""";
    }

    private readonly record struct ComparableVersion(
        int Major,
        int Minor,
        int Patch,
        bool IsPrerelease,
        int PrereleaseNumber) : IComparable<ComparableVersion>
    {
        public int CompareTo(ComparableVersion other)
        {
            var numericComparison = Major.CompareTo(other.Major);
            if (numericComparison != 0)
            {
                return numericComparison;
            }

            numericComparison = Minor.CompareTo(other.Minor);
            if (numericComparison != 0)
            {
                return numericComparison;
            }

            numericComparison = Patch.CompareTo(other.Patch);
            if (numericComparison != 0)
            {
                return numericComparison;
            }

            if (IsPrerelease != other.IsPrerelease)
            {
                return IsPrerelease ? -1 : 1;
            }

            return PrereleaseNumber.CompareTo(other.PrereleaseNumber);
        }
    }
}
