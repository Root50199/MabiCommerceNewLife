using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace MabiCommerceNewLife;

public partial class MainWindow
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private bool _checkingForUpdates;
    private static string CurrentAppVersion => typeof(MainWindow).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    private void InitializeUpdates()
    {
        AppVersionText.Text = $"v{CurrentAppVersion}";
        CheckUpdatesOnOpenCheckBox.IsChecked = _plannerPreferences.CheckUpdatesOnOpen;
        UpdateInfo.Message += $"\n\nCurrent version: {CurrentAppVersion}";
        ContentRendered += CheckUpdatesOnOpen;
        Closing += (_, _) => _updateLifetime.Cancel();
    }

    private void RepositoryLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://github.com/{GitHubUpdateClient.Repository}") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus($"Could not open GitHub repository: {exception.Message}", true);
        }
    }

    private async void CheckUpdatesOnOpen(object? sender, EventArgs e)
    {
        ContentRendered -= CheckUpdatesOnOpen;
        if (_plannerPreferences.CheckUpdatesOnOpen) await CheckForUpdatesAsync(manual: false);
    }

    private void CheckUpdatesOnOpen_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _plannerPreferences.CheckUpdatesOnOpen = CheckUpdatesOnOpenCheckBox.IsChecked == true;
        SavePlannerPreferences();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(manual: true);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdates || _helpClosing) return;
        _checkingForUpdates = true;
        CheckForUpdatesButton.IsEnabled = false;
        string? workspace = null;
        var helperStarted = false;
        try
        {
            if (manual) SetStatus("Checking GitHub for app updates...");
            using var client = new GitHubUpdateClient();
            var release = await client.CheckAsync(CurrentAppVersion, _updateLifetime.Token);
            if (_helpClosing) return;
            if (release is null)
            {
                if (manual) SetStatus($"No newer app release is available. Current version: {CurrentAppVersion}.");
                return;
            }
            if (!ConfirmAppUpdate(release)) return;
            if (_scanCancellation is not null || _autoCalculationCancellation is not null)
                throw new InvalidOperationException("Finish or cancel the current scan/calculation before installing an update.");
            IsEnabled = false;
            workspace = Path.Combine(Path.GetTempPath(), "MabiCommerceUpdates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            SetStatus($"Downloading {release.Tag} from GitHub...");
            var archive = Path.Combine(workspace, "package.zip");
            await client.DownloadAsync(release, archive, _updateLifetime.Token);
            var files = await Task.Run(() => AppUpdateInstaller.ExtractPackage(archive, Path.Combine(workspace, "payload")),
                _updateLifetime.Token);
            if (_helpClosing) return;
            SavePlannerPreferences();
            SetStatus("Update verified. Restarting to install...");
            // From here the helper owns its staging/recovery files, including on failure.
            helperStarted = true;
            await AppUpdateInstaller.StartHelperAsync(workspace, AppContext.BaseDirectory, files);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (_helpClosing) { }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or ArgumentException or OperationCanceledException or
            KeyNotFoundException or FormatException or OverflowException or System.ComponentModel.Win32Exception)
        {
            if (!_helpClosing) SetStatus($"App update failed: {NetworkErrorHelp.Describe(exception, "api.github.com, github.com, release-assets.githubusercontent.com and objects.githubusercontent.com")}" +
                (helperStarted ? $" Recovery files: {workspace}" : string.Empty), true);
        }
        finally
        {
            if (!helperStarted && workspace is not null)
            {
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (!_helpClosing) SetStatus($"Could not remove temporary update files at {workspace}: {exception.Message}", true);
                }
            }
            _checkingForUpdates = false;
            IsEnabled = true;
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private bool ConfirmAppUpdate(AppRelease release) => new AppUpdateWindow(this, release).ShowDialog() == true;
}
