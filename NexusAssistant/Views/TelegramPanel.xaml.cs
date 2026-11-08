using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NexusAssistant.Services;

namespace NexusAssistant.Views;

public partial class TelegramPanel : UserControl
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NexusAssistant";
    private string? _startupCommand;

    public TelegramPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, args) => AppsBox.Text = (args.NewValue as TelegramControl)?.AppsText ?? "";
        Loaded += (_, _) => ReadStartup();
    }

    private void ReadStartup()
    {
        var path = Environment.ProcessPath;
        _startupCommand = string.Equals(Path.GetFileName(path), "NexusAssistant.exe", StringComparison.OrdinalIgnoreCase)
            ? $"\"{path}\" --background" : null;
        StartupSwitch.IsEnabled = _startupCommand is { Length: <= 260 };
        if (!StartupSwitch.IsEnabled)
        {
            StartupStatus.Text = "Для автозапуска откройте NexusAssistant.exe из папки с коротким путём.";
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            StartupSwitch.IsChecked = string.Equals(key?.GetValue(RunValue) as string, _startupCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            StartupSwitch.IsEnabled = false;
            StartupStatus.Text = "Не удалось прочитать настройку автозапуска Windows.";
        }
    }

    private void Startup_Click(object sender, RoutedEventArgs args)
    {
        var enabled = StartupSwitch.IsChecked == true;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(RunValue, _startupCommand!);
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            StartupStatus.Text = enabled ? "NEXUS будет запускаться при входе в Windows." : "Автозапуск выключен.";
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            StartupSwitch.IsChecked = !enabled;
            StartupStatus.Text = "Windows не разрешила изменить автозапуск.";
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is not TelegramControl control) return;
        var token = TokenBox.Password;
        TokenBox.Clear();
        await control.ConnectAsync(token, AppsBox.Text);
    }
    private async void Stop_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is TelegramControl control) await control.StopAsync();
    }
    private void Pair_Click(object sender, RoutedEventArgs args) => (DataContext as TelegramControl)?.NewPairCode();
    private async void Revoke_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is TelegramControl control) await control.RevokeAsync();
    }

    private void PickApp_Click(object sender, RoutedEventArgs args)
    {
        var picker = new OpenFileDialog { Filter = "Приложения Windows (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        var prefix = AppsBox.Text.TrimEnd();
        AppsBox.Text = prefix + (prefix.Length == 0 ? "" : Environment.NewLine) +
                       $"{Path.GetFileNameWithoutExtension(picker.FileName)} = {picker.FileName}";
        AppsBox.CaretIndex = AppsBox.Text.Length;
        AppsBox.Focus();
    }
}
