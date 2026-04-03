using System.Diagnostics;
using System;
using System.Windows;

namespace ZapretManager;

public partial class AboutWindow : Window
{
    private readonly string _authorProfileUrl;
    private readonly string _authorRepositoryUrl;
    private readonly string _flowsealProfileUrl;
    private readonly string _flowsealRepositoryUrl;
    private readonly string _zapretProfileUrl;
    private readonly string _zapretRepositoryUrl;
    private readonly string _issuesUrl;

    public AboutWindow(
        string version,
        string authorProfileUrl,
        string authorRepositoryUrl,
        string flowsealProfileUrl,
        string flowsealRepositoryUrl,
        string zapretProfileUrl,
        string zapretRepositoryUrl,
        string issuesUrl,
        bool useLightTheme)
    {
        InitializeComponent();
        _authorProfileUrl = authorProfileUrl?.Trim() ?? string.Empty;
        _authorRepositoryUrl = authorRepositoryUrl?.Trim() ?? string.Empty;
        _flowsealProfileUrl = flowsealProfileUrl?.Trim() ?? string.Empty;
        _flowsealRepositoryUrl = flowsealRepositoryUrl?.Trim() ?? string.Empty;
        _zapretProfileUrl = zapretProfileUrl?.Trim() ?? string.Empty;
        _zapretRepositoryUrl = zapretRepositoryUrl?.Trim() ?? string.Empty;
        _issuesUrl = issuesUrl?.Trim() ?? string.Empty;
        VersionTextBlock.Text = $"v{version}";

        var hasAuthorLink = !string.IsNullOrWhiteSpace(_authorProfileUrl) || !string.IsNullOrWhiteSpace(_authorRepositoryUrl);
        AuthorGitHubLabelTextBlock.Visibility = hasAuthorLink ? Visibility.Visible : Visibility.Collapsed;
        AuthorGitHubTextBlock.Visibility = hasAuthorLink ? Visibility.Visible : Visibility.Collapsed;
        AuthorProfileRun.Text = GetLastPathSegment(_authorProfileUrl);
        AuthorRepositoryRun.Text = GetLastPathSegment(_authorRepositoryUrl);
        FlowsealProfileRun.Text = GetLastPathSegment(_flowsealProfileUrl);
        FlowsealRepositoryRun.Text = GetLastPathSegment(_flowsealRepositoryUrl);
        ZapretProfileRun.Text = GetLastPathSegment(_zapretProfileUrl);
        ZapretRepositoryRun.Text = GetLastPathSegment(_zapretRepositoryUrl);

        ApplyTheme(useLightTheme);
    }

    private void ApplyTheme(bool useLightTheme)
    {
        SetBrushColor("WindowBgBrush", useLightTheme ? "#F7FBFF" : "#102235");
        SetBrushColor("WindowBorderBrush", useLightTheme ? "#9AB7D3" : "#295276");
        SetBrushColor("TextBrush", useLightTheme ? "#183049" : "#FFFFFF");
        SetBrushColor("MutedBrush", useLightTheme ? "#5E7893" : "#9AB2CD");
        SetBrushColor("PanelBrush", useLightTheme ? "#EEF5FB" : "#0C1A28");
        SetBrushColor("PanelBorderBrush", useLightTheme ? "#9CB7CF" : "#274A6B");
        SetBrushColor("ActionBrush", useLightTheme ? "#DCE9F5" : "#173454");
        SetBrushColor("ActionBorderBrush", useLightTheme ? "#87A8C8" : "#31597F");
        SetBrushColor("LinkBrush", useLightTheme ? "#2C89A1" : "#7BC8FF");
    }

    private void SetBrushColor(string key, string color)
    {
        var convertedColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        if (Resources[key] is System.Windows.Media.SolidColorBrush brush)
        {
            if (brush.Color != convertedColor)
            {
                brush.Color = convertedColor;
            }
        }
        else
        {
            Resources[key] = new System.Windows.Media.SolidColorBrush(convertedColor);
        }
    }

    private void AuthorProfileHyperlink_Click(object sender, RoutedEventArgs e)
    {
        OpenLink(_authorProfileUrl);
    }

    private void AuthorRepositoryHyperlink_Click(object sender, RoutedEventArgs e)
    {
        OpenLink(_authorRepositoryUrl);
    }

    private void FlowsealProfileHyperlink_Click(object sender, RoutedEventArgs e)
    {
        OpenLink(_flowsealProfileUrl);
    }

    private void FlowsealRepositoryHyperlink_Click(object sender, RoutedEventArgs e)
    {
        OpenLink(_flowsealRepositoryUrl);
    }

    private void ZapretProfileHyperlink_Click(object sender, RoutedEventArgs e)
    {
        OpenLink(_zapretProfileUrl);
    }

    private void ZapretRepositoryHyperlink_Click(object sender, RoutedEventArgs e)
    {
        OpenLink(_zapretRepositoryUrl);
    }

    private void IssuesHyperlink_Click(object sender, RoutedEventArgs e)
    {
        OpenLink(_issuesUrl);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DragMove();
    }

    private static void OpenLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static string GetLastPathSegment(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0)
                {
                    return segments[^1];
                }
            }
        }
        catch
        {
        }

        return "GitHub";
    }
}
