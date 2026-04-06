namespace ZapretManager.Models;

public sealed class DiagnosticsReport
{
    public IReadOnlyList<DiagnosticsCheckItem> Items { get; init; } = [];
    public bool NeedsTcpTimestampFix { get; init; }
    public bool HasStaleWinDivert { get; init; }
    public IReadOnlyList<string> ConflictingServices { get; init; } = [];
    public bool HasAnyErrors => Items.Any(item => item.Severity == DiagnosticsSeverity.Error);
    public bool HasAnyWarnings => Items.Any(item => item.Severity == DiagnosticsSeverity.Warning);
}

public sealed class DiagnosticsProgressUpdate
{
    public required string StatusText { get; init; }
    public DiagnosticsCheckItem? Item { get; init; }
    public int CompletedChecks { get; init; }
    public int TotalChecks { get; init; }
}
