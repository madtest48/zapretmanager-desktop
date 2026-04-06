namespace ZapretManager.Models;

public sealed class ManagerUpdateInfo
{
    public required string CurrentVersion { get; init; }

    public required string LatestVersion { get; init; }

    public string? DownloadUrl { get; init; }

    public string? AssetFileName { get; init; }

    public string? ReleasePageUrl { get; init; }

    public bool IsUpdateAvailable { get; init; }
}
