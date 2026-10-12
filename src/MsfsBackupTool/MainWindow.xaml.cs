using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using MsfsBackup.Core;

namespace MsfsBackupTool;

public partial class MainWindow : Window
{
    const string AppTitle = "MSFS Backup Tool";

    readonly ObservableCollection<LocationRow> _locations = new();
    readonly ObservableCollection<RestoreRow> _restoreRows = new();
    readonly Logger _log;
    readonly DispatcherTimer _simTimer;
    List<SimInstall> _installs = new();
    BackupManifest? _manifest;
    string? _manifestFolder;
    CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        _log = Logger.CreateDefault();
        _log.LineWritten += line => Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        LocationsGrid.ItemsSource = _locations;
        RestoreGrid.ItemsSource = _restoreRows;
        DestBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MSFS Backups");
        Title = $"{AppTitle} {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";

        Detect();
        _simTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _simTimer.Tick += (_, _) => UpdateSimBanner();
        _simTimer.Start();
        UpdateSimBanner();
        Closing += OnClosing;
    }

    // ---------- detection ----------

    void Detect()
    {
        _installs = SimLocator.Detect();
        _locations.Clear();
        foreach (var loc in _installs.SelectMany(i => i.Locations))
            _locations.Add(new LocationRow(loc));
        NoInstallsText.Visibility = _installs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _log.Info(_installs.Count == 0
            ? "No MSFS installs detected."
            : "Detected: " + string.Join(", ", _installs.Select(i => i.DisplayName)));
    }

    void UpdateSimBanner()
    {
        string? running = SimProcessGuard.RunningSim();
        SimRunningBanner.Visibility = running == null ? Visibility.Collapsed : Visibility.Visible;
        SimRunningText.Text = $"Microsoft Flight Simulator is running ({running}). Close it before backing up, moving or restoring.";
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Detect();
        if (_manifest != null)
            foreach (var row in _restoreRows)
                row.TargetPath = SimLocator.DefaultRestoreTarget(row.Location, _installs);
    }

    // ---------- backup / move ----------

    void BrowseLocation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LocationRow row) return;
        var d = new OpenFolderDialog { Title = $"Select the {row.FolderName} folder", InitialDirectory = ExistingDir(row.Path) };
        if (d.ShowDialog(this) == true) row.Path = d.FolderName;
    }

    void BrowseDest_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Where should the backup be saved?", InitialDirectory = ExistingDir(DestBox.Text) };
        if (d.ShowDialog(this) == true) DestBox.Text = d.FolderName;
    }

    async void Backup_Click(object sender, RoutedEventArgs e) => await RunBackup(move: false);

    async void Move_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Move out copies the selected folders to the backup location, checks every file, and then deletes the " +
            "originals from the sim folders. If any file fails the check, nothing is deleted.\n\n" +
            "Add-on links are deleted from the Community folders and recreated when you restore. The add-ons they " +
            "point to stay where they are.\n\n" +
            "To keep the sim folders as they are, click No and use Back up instead.\n\nContinue with Move out?",
            AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            await RunBackup(move: true);
    }

    async Task RunBackup(bool move)
    {
        var locations = _locations.Where(r => r.IsSelected).Select(r => r.Location).ToList();
        if (locations.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one folder.", AppTitle);
            return;
        }
        string dest = DestBox.Text.Trim();
        if (dest.Length == 0)
        {
            MessageBox.Show(this, "Choose where to save the backup.", AppTitle);
            return;
        }

        await RunOperation(async (progress, ct) =>
        {
            var result = await Task.Run(() => new BackupEngine(_log, progress, ct).Run(locations, dest, move), ct);
            ShowDone(move ? "Move complete" : "Backup complete");
            string moved = result.EntriesMovedDirectly > 0 ? $"{result.EntriesMovedDirectly} folder(s) moved directly, " : "";
            MessageBox.Show(this,
                $"{(move ? "Move" : "Backup")} complete.\n\n{moved}{result.FilesCopied:N0} files copied ({Fmt.Bytes(result.Bytes)}), " +
                $"{result.LinksRecorded} links recorded.\n\nSaved to:\n{result.BackupFolder}" + WarningsNote(),
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            if (move)
                foreach (var row in _locations) row.RefreshStatus();
            BackupBox.Text = result.BackupFolder;
            LoadBackup(result.BackupFolder, quiet: true);
        });
    }

    // ---------- restore ----------

    void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog
        {
            Title = "Select a backup folder (\"MSFS Backup <date>\")",
            InitialDirectory = ExistingDir(BackupBox.Text.Length > 0 ? BackupBox.Text : DestBox.Text),
        };
        if (d.ShowDialog(this) != true) return;
        BackupBox.Text = d.FolderName;
        LoadBackup(d.FolderName);
    }

    void BackupBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LoadBackup(BackupBox.Text.Trim());
    }

    void BackupBox_LostFocus(object sender, RoutedEventArgs e)
    {
        string path = BackupBox.Text.Trim();
        if (path.Length > 0 && !PathUtil.SamePath(path, _manifestFolder ?? "::")) LoadBackup(path, quiet: true);
    }

    void LoadBackup(string folder, bool quiet = false)
    {
        _manifest = null;
        _manifestFolder = null;
        _restoreRows.Clear();
        if (folder.Length == 0) return;

        // Accept the parent "MSFS Backups" folder too: use its newest backup.
        if (!File.Exists(Path.Combine(folder, BackupManifest.FileName)) && Directory.Exists(folder))
        {
            var newest = new DirectoryInfo(folder).EnumerateDirectories()
                .Where(d => File.Exists(Path.Combine(d.FullName, BackupManifest.FileName)))
                .OrderByDescending(d => d.CreationTimeUtc).FirstOrDefault();
            if (newest != null)
            {
                folder = newest.FullName;
                BackupBox.Text = folder;
            }
        }

        try
        {
            _manifest = BackupManifest.Load(folder);
        }
        catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            BackupInfoText.Text = ex.Message;
            if (!quiet) MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _manifestFolder = folder;
        BackupInfoText.Text = $"{_manifest.Operation} made {_manifest.CreatedUtc.ToLocalTime():f} on {_manifest.MachineName}." +
            (_manifest.Complete ? "" : "  ⚠ This backup didn't finish, so some files may be missing.");
        foreach (var ml in _manifest.Locations)
            _restoreRows.Add(new RestoreRow(ml, SimLocator.DefaultRestoreTarget(ml, _installs)));
        if (!quiet) Tabs.SelectedIndex = 1;
    }

    void BrowseRestoreTarget_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RestoreRow row) return;
        var d = new OpenFolderDialog { Title = $"Restore {row.Name} to…", InitialDirectory = ExistingDir(row.TargetPath) };
        if (d.ShowDialog(this) == true) row.TargetPath = d.FolderName;
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_manifest == null || _manifestFolder == null)
        {
            MessageBox.Show(this, "Choose a backup folder first.", AppTitle);
            return;
        }
        var targets = _restoreRows.Where(r => r.IsSelected).Select(r => new RestoreTarget(r.Location, r.TargetPath.Trim())).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one folder to restore.", AppTitle);
            return;
        }
        var manifest = _manifest;
        string folder = _manifestFolder;
        bool moveBack = MoveBackCheck.IsChecked == true;

        await RunOperation(async (progress, ct) =>
        {
            var result = await Task.Run(() =>
                new RestoreEngine(_log, progress, ct).Run(folder, manifest, targets, ConflictMode.Ask, moveBack, AskConflict), ct);

            if (result.NeedElevation.Count > 0)
                await CreateWithElevation(result.NeedElevation.ToList(), result);
            if (result.DeadLinks.Count > 0)
                await RepointDeadLinks(result);

            ShowDone("Restore complete");
            MessageBox.Show(this, "Restore complete.\n\n" + result.Summary() + WarningsNote(), AppTitle,
                MessageBoxButton.OK, result.Errors > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            foreach (var row in _locations) row.RefreshStatus();
        });
    }

    ConflictMode? AskConflict(ConflictSummary summary) => Dispatcher.Invoke(() =>
    {
        var d = new ConflictDialog(summary) { Owner = this };
        return d.ShowDialog() == true ? d.Choice : (ConflictMode?)null;
    });

    /// <summary>Asks once, then creates symbolic links from an elevated helper.</summary>
    async Task CreateWithElevation(List<PendingLink> links, RestoreResult result, IReadOnlyList<string>? newTargets = null)
    {
        result.NeedElevation.RemoveAll(links.Contains);
        var answer = MessageBox.Show(this,
            $"{links.Count} add-on link(s) are symbolic links. Windows needs administrator rights to create those.\n\n" +
            "Show the administrator prompt now?", AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            result.NeedElevation.AddRange(links);
            _log.Warn($"{links.Count} symbolic link(s) not created (admin rights declined).");
            return;
        }

        var requests = links.Select((l, i) => l.ToRequest(newTargets?[i])).ToList();
        var outcomes = await Task.Run(() => LinkCreator.CreateElevated(requests));
        foreach (var r in outcomes)
        {
            if (r.Success)
            {
                result.LinksCreated++;
            }
            else
            {
                result.Errors++;
                _log.Error($"Couldn't create link \"{r.LinkPath}\": {r.Error}");
            }
        }
    }

    async Task RepointDeadLinks(RestoreResult result)
    {
        var dialog = new DeadLinksDialog(result.DeadLinks) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Selected.Count == 0)
        {
            _log.Info($"{result.DeadLinks.Count} link(s) with a missing target were skipped.");
            return;
        }

        var elevate = new List<(PendingLink Link, string Target)>();
        foreach (var (link, target) in dialog.Selected)
        {
            var r = LinkCreator.TryCreate(link.ToRequest(target));
            if (r.Success)
            {
                result.LinksCreated++;
                result.DeadLinks.Remove(link);
                _log.Info($"Link \"{link.LinkPath}\" re-pointed to {target}.");
            }
            else if (r.NeedsElevation)
            {
                elevate.Add((link, target));
            }
            else
            {
                result.Errors++;
                _log.Error($"Couldn't create link \"{link.LinkPath}\": {r.Error}");
            }
        }
        if (elevate.Count > 0)
        {
            foreach (var (link, _) in elevate) result.DeadLinks.Remove(link);
            await CreateWithElevation(elevate.Select(x => x.Link).ToList(), result, elevate.Select(x => x.Target).ToList());
        }
        if (result.DeadLinks.Count > 0)
            _log.Info($"{result.DeadLinks.Count} link(s) with a missing target were skipped.");
    }

    // ---------- shared plumbing ----------

    async Task RunOperation(Func<IProgress<ProgressInfo>, CancellationToken, Task> body)
    {
        if (SimProcessGuard.RunningSim() is string exe)
        {
            MessageBox.Show(this, $"Microsoft Flight Simulator is running ({exe}). Close the sim first.", AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<ProgressInfo>(ShowProgress);
        try
        {
            await body(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Cancelled by user.");
            ShowDone("Cancelled");
        }
        catch (UserFacingException ex)
        {
            _log.Error(ex.Message);
            ShowDone("Stopped");
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
            ShowDone("Failed");
            MessageBox.Show(this, $"Unexpected error:\n\n{ex.Message}\n\nDetails are in the log.", AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        BackupButton.IsEnabled = MoveButton.IsEnabled = RestoreButton.IsEnabled = RefreshButton.IsEnabled = !busy;
        LocationsGrid.IsEnabled = RestoreGrid.IsEnabled = DestBox.IsEnabled = BackupBox.IsEnabled = MoveBackCheck.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        if (busy)
        {
            Bar.Value = 0;
            ItemText.Text = DetailText.Text = "";
        }
        else
        {
            Bar.IsIndeterminate = false;
        }
    }

    void ShowProgress(ProgressInfo p)
    {
        PhaseText.Text = p.Phase;
        ItemText.Text = p.Item ?? "";
        if (p.BytesTotal > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 100.0 * p.BytesDone / p.BytesTotal;
            DetailText.Text = $"{p.FilesDone:N0} / {p.FilesTotal:N0} files   {Fmt.Bytes(p.BytesDone)} / {Fmt.Bytes(p.BytesTotal)}";
        }
        else if (p.FilesTotal > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 100.0 * p.FilesDone / p.FilesTotal;
            DetailText.Text = $"{p.FilesDone:N0} / {p.FilesTotal:N0}";
        }
        else
        {
            Bar.IsIndeterminate = true;
            DetailText.Text = "";
        }
    }

    void ShowDone(string text)
    {
        PhaseText.Text = text;
        Bar.IsIndeterminate = false;
        Bar.Value = text.EndsWith("complete") ? 100 : 0;
        ItemText.Text = "";
    }

    string WarningsNote() =>
        _log.Warnings + _log.Errors > 0 ? $"\n\n{_log.Warnings} warning(s), {_log.Errors} error(s). See the log for details." : "";

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        PhaseText.Text = "Cancelling…";
        CancelButton.IsEnabled = false;
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cts == null) return;
        var answer = MessageBox.Show(this, "An operation is still running. Cancel it and close?", AppTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        e.Cancel = true; // let the operation unwind before closing
        if (answer == MessageBoxResult.Yes)
        {
            _cts.Cancel();
            _ = CloseWhenIdle();
        }
    }

    async Task CloseWhenIdle()
    {
        while (_cts != null) await Task.Delay(200);
        Close();
    }

    void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Logger.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Logger.LogDirectory}\"") { UseShellExecute = true });
    }

    static string ExistingDir(string path)
    {
        try
        {
            for (string? p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if (Directory.Exists(p)) return p;
        }
        catch (ArgumentException) { }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}
