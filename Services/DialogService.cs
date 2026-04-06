using System.Windows;
using ZapretManager.ViewModels;
using System.ComponentModel;

namespace ZapretManager.Services;

public static class DialogService
{
    private const string DefaultErrorMessage = "Произошла ошибка. Закройте программу и попробуйте снова.";
    private const string MovedManagerMessage = "Файл ZapretManager был перемещён после запуска. Закройте программу через трей или Диспетчер задач и откройте её заново из новой папки.";

    public readonly record struct ConfirmResult(bool Accepted, bool RememberChoice);
    public enum DialogChoice
    {
        Closed,
        Primary,
        Secondary,
        Tertiary
    }

    public static void ShowInfo(string message, string title = "Zapret Manager", Window? owner = null)
    {
        ShowDialog(title, message, isError: false, owner, DialogButtons.Ok);
    }

    public static void ShowError(string message, string title = "Zapret Manager", Window? owner = null)
    {
        ShowDialog(title, NormalizeErrorMessage(message), isError: true, owner, DialogButtons.Ok);
    }

    public static void ShowError(Exception exception, string title = "Zapret Manager", Window? owner = null)
    {
        ShowDialog(title, GetDisplayMessage(exception), isError: true, owner, DialogButtons.Ok);
    }

    public static bool Confirm(string message, string title = "Zapret Manager", Window? owner = null)
    {
        return ShowDialog(title, message, isError: false, owner, DialogButtons.YesNo) == true;
    }

    public static ConfirmResult ConfirmWithRemember(
        string message,
        string title = "Zapret Manager",
        Window? owner = null,
        string rememberText = "Больше не спрашивать")
    {
        var dialog = CreateDialog(title, message, isError: false, owner, DialogButtons.YesNo, rememberText, primaryButtonText: null, secondaryButtonText: null, tertiaryButtonText: null);
        var accepted = dialog.ShowDialog() == true;
        return new ConfirmResult(accepted, accepted && dialog.RememberChoice);
    }

    public static bool ConfirmCustom(
        string message,
        string title = "Zapret Manager",
        Window? owner = null,
        string primaryButtonText = "Да",
        string secondaryButtonText = "Нет")
    {
        var dialog = CreateDialog(
            title,
            message,
            isError: false,
            owner,
            DialogButtons.YesNo,
            rememberText: null,
            primaryButtonText,
            secondaryButtonText,
            tertiaryButtonText: null);
        return dialog.ShowDialog() == true;
    }

    public static DialogChoice ChooseCustom(
        string message,
        string title = "Zapret Manager",
        Window? owner = null,
        string primaryButtonText = "Да",
        string secondaryButtonText = "Нет",
        string tertiaryButtonText = "Отмена")
    {
        var dialog = CreateDialog(
            title,
            message,
            isError: false,
            owner,
            DialogButtons.YesNo,
            rememberText: null,
            primaryButtonText,
            secondaryButtonText,
            tertiaryButtonText);
        _ = dialog.ShowDialog();
        return dialog.Choice;
    }

    public static DeleteZapretChoice ChooseDeleteZapretMode(
        string rootPath,
        string title = "Zapret Manager",
        Window? owner = null)
    {
        var windowOwner = owner ?? System.Windows.Application.Current?.MainWindow;
        var useLightTheme = windowOwner is MainWindow mainWindow
            ? mainWindow.CurrentUseLightTheme
            : GetUseLightTheme();

        var dialog = new DeleteZapretChoiceWindow(rootPath, useLightTheme)
        {
            Title = title
        };

        if (windowOwner is not null && windowOwner.IsLoaded)
        {
            dialog.Owner = windowOwner;
        }

        dialog.ShowDialog();
        return dialog.Choice;
    }

    private static bool? ShowDialog(string title, string message, bool isError, Window? owner, DialogButtons buttons)
    {
        var dialog = CreateDialog(title, message, isError, owner, buttons, rememberText: null, primaryButtonText: null, secondaryButtonText: null, tertiaryButtonText: null);
        return dialog.ShowDialog();
    }

    private static ThemedDialogWindow CreateDialog(
        string title,
        string message,
        bool isError,
        Window? owner,
        DialogButtons buttons,
        string? rememberText,
        string? primaryButtonText,
        string? secondaryButtonText,
        string? tertiaryButtonText)
    {
        var windowOwner = owner ?? System.Windows.Application.Current?.MainWindow;
        var useLightTheme = windowOwner is MainWindow mainWindow
            ? mainWindow.CurrentUseLightTheme
            : GetUseLightTheme();
        var dialog = new ThemedDialogWindow(title, message, isError, buttons, useLightTheme, rememberText, primaryButtonText, secondaryButtonText, tertiaryButtonText);
        if (windowOwner is not null && windowOwner.IsLoaded)
        {
            dialog.Owner = windowOwner;
        }

        return dialog;
    }

    public static bool GetUseLightTheme()
    {
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel viewModel)
        {
            return viewModel.UseLightThemeEnabled;
        }

        return new AppSettingsService().Load().UseLightTheme;
    }

    public static string GetDisplayMessage(Exception exception, string? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        while (exception is AggregateException aggregateException && aggregateException.InnerExceptions.Count == 1)
        {
            exception = aggregateException.InnerExceptions[0];
        }

        if (exception is System.Reflection.TargetInvocationException targetInvocationException &&
            targetInvocationException.InnerException is not null)
        {
            exception = targetInvocationException.InnerException;
        }

        if (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return MovedManagerMessage;
        }

        if (exception is Win32Exception win32Exception && win32Exception.NativeErrorCode == 2)
        {
            return MovedManagerMessage;
        }

        var message = exception.Message;
        return NormalizeErrorMessage(message, fallback ?? DefaultErrorMessage);
    }

    public static string NormalizeErrorMessage(string? message, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? DefaultErrorMessage : fallback.Trim();
    }

    public enum DialogButtons
    {
        Ok,
        YesNo
    }
}
