using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MultiSSH.Models;
using MultiSSH.Services;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MultiSSH.Views;

/// <summary>
/// A WinSCP-style graphical file manager for a single SFTP connection: two panes
/// side by side — the local file system on the left, the remote host on the right —
/// with a toolbar for the common operations (transfer, refresh, new folder, rename,
/// delete). Hosted by <see cref="SessionView"/> in place of the terminal when the
/// session's <see cref="SessionKind"/> is <see cref="SessionKind.Wscp"/>.
/// </summary>
public class WscpPanel : Grid
{
    private readonly SessionConfig _cfg;
    private SftpClient? _sftp;

    private string _remoteCwd = "/";
    private string _localCwd;
    private bool _connecting;
    private bool _busy;
    private string _connectedStatus = "";
    /// <summary>Last completed operation's result line (e.g. "Uploaded 3 item(s)"), kept
    /// visible through the auto-refresh that follows it. Null falls back to the idle line.</summary>
    private string? _lastResult;
    /// <summary>Throttle timer for per-file transfer progress updates.</summary>
    private readonly System.Diagnostics.Stopwatch _progWatch = new();

    private readonly ObservableCollection<FsItem> _localItems = new();
    private readonly ObservableCollection<FsItem> _remoteItems = new();

    private readonly ListView _localList;
    private readonly ListView _remoteList;
    private readonly TextBox _localPathBox;
    private readonly TextBox _remotePathBox;
    private readonly TextBlock _remoteHeader;

    /// <summary>Connection state + status text, surfaced to the hosting SessionView's status strip.</summary>
    public event Action<ConnectionState, string>? StateChanged;

    private const int MaxAuthAttempts = 4;

    public WscpPanel(SessionConfig cfg)
    {
        _cfg = cfg;
        _localCwd = SafeStartDir();

        // Local pane | transfer buttons | Remote pane. Each pane carries its own toolbar
        // (Up / Refresh / New / Rename / Delete) that acts explicitly on that side, so an
        // action can never land on the wrong pane.
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _localList = MakeList(_localItems);
        _localPathBox = MakePathBox();
        var localHeader = MakeHeaderText("This PC (local)");
        var localPane = BuildPane(localHeader, _localPathBox, _localList, remote: false);
        Grid.SetColumn(localPane, 0);
        body.Children.Add(localPane);

        var transfer = BuildTransferColumn();
        Grid.SetColumn(transfer, 1);
        body.Children.Add(transfer);

        _remoteList = MakeList(_remoteItems);
        _remotePathBox = MakePathBox();
        _remoteHeader = MakeHeaderText(RemoteTitle());
        var remotePane = BuildPane(_remoteHeader, _remotePathBox, _remoteList, remote: true);
        Grid.SetColumn(remotePane, 2);
        body.Children.Add(remotePane);

        Children.Add(body);

        // Local side can be listed immediately, before the connection is up.
        RefreshLocal();
    }

    // -------------------- connection --------------------

    public async Task ConnectAsync()
    {
        if (_connecting) return;
        _connecting = true;
        try { await ConnectCoreAsync(); }
        finally { _connecting = false; }
    }

    public async Task ReconnectAsync()
    {
        DisconnectClient();
        await ConnectAsync();
    }

    private async Task ConnectCoreAsync()
    {
        Emit(ConnectionState.Connecting, "Connecting…");

        // Ask for a password up front if this method needs one and none was saved
        // (a key file means public-key auth, so never prompt for a password there).
        if (UsesPassword(_cfg.Auth) && string.IsNullOrEmpty(_cfg.Password)
            && string.IsNullOrEmpty(_cfg.PrivateKeyPath))
        {
            var pw = PromptSecret($"Password for {_cfg.Username}@{_cfg.Host}", $"{_cfg.Host} : {_cfg.Port}", null);
            if (pw == null) { Emit(ConnectionState.Disconnected, "Login cancelled — no password entered"); return; }
            _cfg.Password = pw;
        }

        for (int attempt = 1; ; attempt++)
        {
            SftpClient? client = null;
            try
            {
                await Task.Run(() =>
                {
                    client = new SftpClient(RemoteAuth.BuildConnectionInfo(_cfg));
                    client.Connect();
                    RemoteAuth.ApplySocketOptions(client, _cfg);
                });

                _sftp = client;
                _remoteCwd = _sftp!.WorkingDirectory;
                _connectedStatus = $"Connected — {_cfg.Username}@{_cfg.Host}";
                Emit(ConnectionState.Connected, _connectedStatus);
                RefreshRemote();
                return;
            }
            catch (SshAuthenticationException ex)
            {
                client?.Dispose();
                if (!UsesPassword(_cfg.Auth) || attempt >= MaxAuthAttempts) { Fail(ex.Message); return; }
                var pw = PromptSecret($"Password for {_cfg.Username}@{_cfg.Host}",
                    $"{_cfg.Host} : {_cfg.Port}", $"Authentication failed: {ex.Message}");
                if (pw == null) { Emit(ConnectionState.Disconnected, "Login cancelled"); return; }
                _cfg.Password = pw;
            }
            catch (KeyPassphraseRequiredException ex)
            {
                client?.Dispose();
                if (attempt >= MaxAuthAttempts) { Fail(ex.Message); return; }
                string? error = string.IsNullOrEmpty(_cfg.KeyPassphrase) ? null : ex.Message;
                var pass = PromptSecret("Passphrase for the private key", _cfg.PrivateKeyPath ?? "", error);
                if (pass == null) { Emit(ConnectionState.Disconnected, "Login cancelled"); return; }
                _cfg.KeyPassphrase = pass;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                Fail(ex.Message);
                return;
            }
        }
    }

    private static bool UsesPassword(AuthMethod m)
        => m is AuthMethod.Password or AuthMethod.KeyboardInteractive or AuthMethod.Agent;

    private string? PromptSecret(string prompt, string target, string? error)
    {
        var dlg = new PasswordPromptDialog(prompt, target, error) { Owner = Window.GetWindow(this) };
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    private void Fail(string msg) => Emit(ConnectionState.Failed, "Connection failed: " + msg);

    private void Emit(ConnectionState s, string text)
        => Dispatcher.Invoke(() => StateChanged?.Invoke(s, text));

    /// <summary>Report a transient status line while keeping the connected (green) state.</summary>
    private void Status(string text) => Emit(ConnectionState.Connected, text);

    /// <summary>Restore the steady "Connected — user@host" status line.</summary>
    private void StatusIdle() => Emit(ConnectionState.Connected, _connectedStatus);

    /// <summary>Show the last operation's result if one is pending (so a transfer's
    /// completion notice survives the refresh that follows it), else the idle line.</summary>
    private void Settle() => Emit(ConnectionState.Connected, _lastResult ?? _connectedStatus);

    public void FocusDefault() => (_sftp != null ? _remoteList : _localList).Focus();

    public void Shutdown() => DisconnectClient();

    private void DisconnectClient()
    {
        var s = _sftp;
        _sftp = null;
        if (s != null) Task.Run(() => { try { s.Disconnect(); } catch { } try { s.Dispose(); } catch { } });
    }

    // -------------------- background operation runner --------------------

    /// <summary>Run a blocking SFTP operation off the UI thread, then refresh both panes.
    /// Serialised via <see cref="_busy"/> so operations never overlap on the single client.</summary>
    private async Task DoAsync(string busyText, Action work, bool refreshRemote = true, bool refreshLocal = true)
    {
        if (_sftp == null || !_sftp.IsConnected) { Status("Not connected"); return; }
        if (_busy) return;
        _busy = true;
        _lastResult = null;
        Status(busyText);
        try
        {
            await Task.Run(work);
            Settle();
        }
        catch (Exception ex)
        {
            Status("Error: " + ex.Message);
            MessageBox.Show(Window.GetWindow(this), ex.Message, "WSCP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; }

        if (refreshRemote) RefreshRemote();
        if (refreshLocal) RefreshLocal();
    }

    // -------------------- remote listing / navigation --------------------

    private async void RefreshRemote()
    {
        // SSH.NET's SftpClient shares one channel and is not safe for concurrent requests,
        // so every remote listing is serialised through _busy — the same flag that guards
        // transfers. Running the listing on a bare thread (as before) let a manual Refresh
        // overlap another request and wedge the channel, after which Refresh silently did
        // nothing. A refresh requested while an op is already running is dropped on purpose:
        // that op refreshes both panes when it finishes.
        var sftp = _sftp;                       // capture: the pane may close and null the field
        if (sftp == null || !sftp.IsConnected) { Status("Not connected"); return; }
        if (_busy) return;
        _busy = true;
        Status("Refreshing…");
        try
        {
            var cwd = _remoteCwd;
            var list = await Task.Run(() => sftp.ListDirectory(cwd).ToList());
            PopulateRemote(list);
            Settle();
        }
        catch (Exception ex)
        {
            Status("Error: " + ex.Message);
        }
        finally { _busy = false; }
    }

    private void PopulateRemote(List<ISftpFile> files)
    {
        _remoteItems.Clear();
        if (_remoteCwd != "/") _remoteItems.Add(FsItem.Up());
        foreach (var f in files.Where(f => f.IsDirectory && f.Name is not "." and not "..")
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            _remoteItems.Add(new FsItem(f.Name, true, 0, f.LastWriteTime));
        foreach (var f in files.Where(f => !f.IsDirectory)
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            _remoteItems.Add(new FsItem(f.Name, false, f.Length, f.LastWriteTime));
        _remotePathBox.Text = _remoteCwd;
        _remoteHeader.Text = RemoteTitle();
    }

    private async void NavigateRemote(string path)
    {
        await DoAsync($"Opening {path}…", () =>
        {
            _sftp!.ChangeDirectory(path);
            _remoteCwd = _sftp.WorkingDirectory;
        }, refreshLocal: false);
    }

    // -------------------- local listing / navigation --------------------

    private void RefreshLocal()
    {
        _localItems.Clear();
        if (Directory.GetParent(_localCwd) != null) _localItems.Add(FsItem.Up());
        try
        {
            foreach (var d in Directory.GetDirectories(_localCwd)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var di = new DirectoryInfo(d);
                _localItems.Add(new FsItem(di.Name, true, 0, di.LastWriteTime));
            }
            foreach (var f in Directory.GetFiles(_localCwd)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(f);
                _localItems.Add(new FsItem(fi.Name, false, fi.Length, fi.LastWriteTime));
            }
        }
        catch (Exception ex) { Status("Local: " + ex.Message); }
        _localPathBox.Text = _localCwd;
    }

    private void NavigateLocal(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) { Status("No such local folder: " + full); return; }
            _localCwd = full;
            RefreshLocal();
        }
        catch (Exception ex) { Status("Local: " + ex.Message); }
    }

    // -------------------- transfers --------------------

    private async void UploadSelected()
    {
        var items = SelectedLocal();
        if (items.Count == 0) { Status("Select local files to upload"); return; }
        await DoAsync($"Uploading {items.Count} item(s)…", () =>
        {
            for (int i = 0; i < items.Count; i++)
                UploadPath(Path.Combine(_localCwd, items[i].Name),
                    CombineRemote(_remoteCwd, items[i].Name), i + 1, items.Count);
            _lastResult = $"✔ Uploaded {items.Count} item(s) to {_remoteCwd}";
        });
    }

    private void UploadPath(string localPath, string remotePath, int idx, int total)
    {
        if (Directory.Exists(localPath))
        {
            EnsureRemoteDir(remotePath);
            foreach (var d in Directory.GetDirectories(localPath))
                UploadPath(d, CombineRemote(remotePath, Path.GetFileName(d)), idx, total);
            foreach (var f in Directory.GetFiles(localPath))
                UploadPath(f, CombineRemote(remotePath, Path.GetFileName(f)), idx, total);
        }
        else
        {
            long size = new FileInfo(localPath).Length;
            string name = Path.GetFileName(localPath);
            _progWatch.Reset();                 // first callback of a new file reports at once
            using var fs = File.OpenRead(localPath);
            _sftp!.UploadFile(fs, remotePath, canOverride: true,
                sent => ReportTransfer("Uploading", name, idx, total, sent, size));
        }
    }

    private void EnsureRemoteDir(string path)
    {
        try { if (!_sftp!.Exists(path)) _sftp.CreateDirectory(path); }
        catch { /* may already exist */ }
    }

    private async void DownloadSelected()
    {
        var items = SelectedRemote();
        if (items.Count == 0) { Status("Select remote files to download"); return; }
        await DoAsync($"Downloading {items.Count} item(s)…", () =>
        {
            for (int i = 0; i < items.Count; i++)
                DownloadPath(CombineRemote(_remoteCwd, items[i].Name),
                    Path.Combine(_localCwd, items[i].Name), items[i].IsDirectory, i + 1, items.Count);
            _lastResult = $"✔ Downloaded {items.Count} item(s) to {_localCwd}";
        });
    }

    private void DownloadPath(string remotePath, string localPath, bool isDir, int idx, int total)
    {
        if (isDir)
        {
            Directory.CreateDirectory(localPath);
            foreach (var f in _sftp!.ListDirectory(remotePath))
            {
                if (f.Name is "." or "..") continue;
                DownloadPath(CombineRemote(remotePath, f.Name), Path.Combine(localPath, f.Name), f.IsDirectory, idx, total);
            }
        }
        else
        {
            string name = Path.GetFileName(localPath);
            long size = 0;
            try { size = _sftp!.GetAttributes(remotePath).Size; } catch { /* size unknown → show bytes */ }
            _progWatch.Reset();                 // first callback of a new file reports at once
            using var fs = File.Create(localPath);
            _sftp!.DownloadFile(remotePath, fs,
                got => ReportTransfer("Downloading", name, idx, total, got, size));
        }
    }

    /// <summary>Throttled per-file transfer progress on the status strip. Called from the
    /// SSH.NET transfer thread on each buffer; limited to a few updates a second so the
    /// dispatcher isn't flooded, but always fires once the file is complete.</summary>
    private void ReportTransfer(string verb, string name, int idx, int total, ulong done, long size)
    {
        bool finished = size <= 0 || (long)done >= size;
        if (_progWatch.IsRunning && _progWatch.ElapsedMilliseconds < 120 && !finished) return;
        _progWatch.Restart();
        string amount = size > 0
            ? $"{(long)done * 100 / size}%  ({FmtBytes((long)done)} / {FmtBytes(size)})"
            : FmtBytes((long)done);
        string count = total > 1 ? $" [{idx}/{total}]" : "";
        Status($"{verb}{count}  {name} — {amount}");
    }

    private static string FmtBytes(long b) =>
        b < 1024 ? $"{b} B" :
        b < 1024L * 1024 ? $"{b / 1024.0:0.#} KB" :
        b < 1024L * 1024 * 1024 ? $"{b / 1024.0 / 1024:0.#} MB" :
        $"{b / 1024.0 / 1024 / 1024:0.#} GB";

    // -------------------- file operations (each button says which side it targets) --------------------

    private async void NewFolder(bool remote)
    {
        var name = InputDialog.Ask(Window.GetWindow(this), "New Folder",
            $"New folder in the {(remote ? "remote" : "local")} directory:");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        if (remote)
            await DoAsync($"Creating {name}…", () => _sftp!.CreateDirectory(CombineRemote(_remoteCwd, name)), refreshLocal: false);
        else
        {
            try { Directory.CreateDirectory(Path.Combine(_localCwd, name)); } catch (Exception ex) { Status("Local: " + ex.Message); }
            RefreshLocal();
        }
    }

    private async void RenameSelected(bool remote)
    {
        var it = (remote ? SelectedRemote() : SelectedLocal()).FirstOrDefault();
        if (it == null) { Status("Select an item to rename"); return; }
        var name = InputDialog.Ask(Window.GetWindow(this), "Rename", "New name:", it.Name);
        if (string.IsNullOrWhiteSpace(name) || name == it.Name) return;
        name = name.Trim();

        if (remote)
            await DoAsync($"Renaming {it.Name}…",
                () => _sftp!.RenameFile(CombineRemote(_remoteCwd, it.Name), CombineRemote(_remoteCwd, name)),
                refreshLocal: false);
        else
        {
            try
            {
                var src = Path.Combine(_localCwd, it.Name);
                var dst = Path.Combine(_localCwd, name);
                if (it.IsDirectory) Directory.Move(src, dst); else File.Move(src, dst);
            }
            catch (Exception ex) { Status("Local: " + ex.Message); }
            RefreshLocal();
        }
    }

    private async void DeleteSelected(bool remote)
    {
        var items = remote ? SelectedRemote() : SelectedLocal();
        if (items.Count == 0) { Status("Select item(s) to delete"); return; }

        var where = remote ? "remote" : "local";
        if (MessageBox.Show(Window.GetWindow(this),
                $"Delete {items.Count} {where} item(s)? This cannot be undone.",
                "WSCP — Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (remote)
            await DoAsync($"Deleting {items.Count} item(s)…", () =>
            {
                foreach (var it in items) DeleteRemote(CombineRemote(_remoteCwd, it.Name), it.IsDirectory);
            }, refreshLocal: false);
        else
        {
            try
            {
                foreach (var it in items)
                {
                    var p = Path.Combine(_localCwd, it.Name);
                    if (it.IsDirectory) Directory.Delete(p, recursive: true); else File.Delete(p);
                }
            }
            catch (Exception ex) { Status("Local: " + ex.Message); }
            RefreshLocal();
        }
    }

    private void DeleteRemote(string path, bool isDir)
    {
        if (isDir)
        {
            foreach (var f in _sftp!.ListDirectory(path))
            {
                if (f.Name is "." or "..") continue;
                DeleteRemote(CombineRemote(path, f.Name), f.IsDirectory);
            }
            _sftp.DeleteDirectory(path);
        }
        else _sftp!.DeleteFile(path);
    }

    // -------------------- selection helpers --------------------

    private List<FsItem> SelectedLocal() => _localList.SelectedItems.Cast<FsItem>().Where(i => !i.IsUp).ToList();
    private List<FsItem> SelectedRemote() => _remoteList.SelectedItems.Cast<FsItem>().Where(i => !i.IsUp).ToList();

    private static string CombineRemote(string dir, string name)
        => dir.TrimEnd('/') + "/" + name;

    private static string SafeStartDir()
    {
        try
        {
            var d = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists(d) ? d : Directory.GetCurrentDirectory();
        }
        catch { return @"C:\"; }
    }

    private string RemoteTitle()
        => string.IsNullOrWhiteSpace(_cfg.Host) ? "Remote" : $"Remote — {_cfg.Host}";

    // -------------------- UI construction --------------------

    private FrameworkElement BuildTransferColumn()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        };
        var up = ToolButton("▶", "Upload selected local items to the remote folder", (_, _) => UploadSelected());
        var down = ToolButton("◀", "Download selected remote items to the local folder", (_, _) => DownloadSelected());
        up.MinWidth = down.MinWidth = 34;
        up.Margin = down.Margin = new Thickness(0, 3, 0, 3);
        panel.Children.Add(up);
        panel.Children.Add(down);
        return panel;
    }

    private Button ToolButton(string glyph, string tip, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = glyph,
            ToolTip = tip,
            MinWidth = 30,
            Height = 24,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
        };
        b.Click += onClick;
        return b;
    }

    private FrameworkElement BuildPane(TextBlock header, TextBox pathBox, ListView list, bool remote)
    {
        var dock = new DockPanel { Margin = new Thickness(3) };

        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        // Per-pane toolbar — every button acts explicitly on THIS side.
        string side = remote ? "the remote host" : "this PC";
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        tools.Children.Add(ToolButton("↑", "Up one folder",
            (_, _) => { if (remote) NavigateRemote(".."); else NavigateLocal(ParentLocal()); }));
        tools.Children.Add(ToolButton("⟳", "Refresh this pane",
            (_, _) => { if (remote) RefreshRemote(); else RefreshLocal(); }));
        tools.Children.Add(ToolButton("＋", $"New folder on {side}", (_, _) => NewFolder(remote)));
        tools.Children.Add(ToolButton("✎", $"Rename the selected item on {side}", (_, _) => RenameSelected(remote)));
        tools.Children.Add(ToolButton("🗑", $"Delete the selected item(s) on {side}", (_, _) => DeleteSelected(remote)));
        DockPanel.SetDock(tools, Dock.Top);
        dock.Children.Add(tools);

        pathBox.Margin = new Thickness(0, 0, 0, 4);
        pathBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (remote) NavigateRemote(pathBox.Text.Trim());
            else NavigateLocal(pathBox.Text.Trim());
            e.Handled = true;
        };
        DockPanel.SetDock(pathBox, Dock.Top);
        dock.Children.Add(pathBox);

        dock.Children.Add(list);

        var border = new Border { BorderThickness = new Thickness(1), Child = dock };
        border.SetResourceReference(Border.BorderBrushProperty, ThemeManager.ButtonBorder);
        return border;
    }

    private string ParentLocal()
        => Directory.GetParent(_localCwd)?.FullName ?? _localCwd;

    private ListView MakeList(ObservableCollection<FsItem> source)
    {
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Name", DisplayMemberBinding = new Binding(nameof(FsItem.Display)), Width = 240 });
        gv.Columns.Add(new GridViewColumn { Header = "Size", DisplayMemberBinding = new Binding(nameof(FsItem.SizeText)), Width = 80 });
        gv.Columns.Add(new GridViewColumn { Header = "Modified", DisplayMemberBinding = new Binding(nameof(FsItem.ModifiedText)), Width = 120 });

        var lv = new ListView
        {
            View = gv,
            ItemsSource = source,
            SelectionMode = SelectionMode.Extended,
            BorderThickness = new Thickness(0),
        };
        // Active-pane tracking is handled at the pane border (BuildPane) so a click
        // anywhere in the pane counts, not only on the list rows.
        bool remote = ReferenceEquals(source, _remoteItems);
        lv.MouseDoubleClick += (_, _) => OnItemActivated(remote, lv.SelectedItem as FsItem);
        return lv;
    }

    private void OnItemActivated(bool remote, FsItem? item)
    {
        if (item == null) return;
        if (remote)
        {
            if (item.IsUp) NavigateRemote("..");
            else if (item.IsDirectory) NavigateRemote(CombineRemote(_remoteCwd, item.Name));
            else DownloadSelected();   // double-click a remote file → download it
        }
        else
        {
            if (item.IsUp) NavigateLocal(ParentLocal());
            else if (item.IsDirectory) NavigateLocal(Path.Combine(_localCwd, item.Name));
            else UploadSelected();     // double-click a local file → upload it
        }
    }

    private TextBox MakePathBox()
        => new() { VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Type a path and press Enter to navigate" };

    private TextBlock MakeHeaderText(string text)
    {
        var t = new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(2, 0, 0, 4),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, ThemeManager.Text);
        return t;
    }

    // -------------------- item model --------------------

    /// <summary>One row in a file pane (a folder, a file, or the "up one level" entry).</summary>
    public sealed class FsItem
    {
        public string Name { get; }
        public bool IsDirectory { get; }
        public bool IsUp { get; }
        public long Length { get; }
        public DateTime Modified { get; }

        public FsItem(string name, bool isDirectory, long length, DateTime modified, bool isUp = false)
        {
            Name = name;
            IsDirectory = isDirectory;
            Length = length;
            Modified = modified;
            IsUp = isUp;
        }

        public static FsItem Up() => new("..", true, 0, default, isUp: true);

        public string Display => IsUp ? "📁 .." : (IsDirectory ? "📁 " : "📄 ") + Name;

        public string SizeText => IsDirectory ? "" : Human(Length);

        public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

        private static string Human(long b) =>
            b < 1024 ? $"{b} B" :
            b < 1024L * 1024 ? $"{b / 1024.0:0.#} KB" :
            b < 1024L * 1024 * 1024 ? $"{b / 1024.0 / 1024:0.#} MB" :
            $"{b / 1024.0 / 1024 / 1024:0.#} GB";
    }
}
