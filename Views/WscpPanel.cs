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
    private bool _shutdown;
    /// <summary>Serialises every request on the single SFTP channel (listings and operations).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _opRunning;
    private Task? _currentOp;
    /// <summary>Cancelled on disconnect so a multi-file operation stops between files.</summary>
    private CancellationTokenSource _cts = new();
    private string _connectedStatus = "";
    /// <summary>Last completed operation's result line (e.g. "Uploaded 3 item(s)"), kept
    /// visible through the auto-refresh that follows it. Null falls back to the idle line.</summary>
    private string? _lastResult;
    /// <summary>Throttle timer for per-file transfer progress updates.</summary>
    private readonly System.Diagnostics.Stopwatch _progWatch = new();
    /// <summary>A transfer cut off by a dropped link (or by Reconnect mid-transfer). It carries
    /// on from where its files stopped as soon as the session is connected again.</summary>
    private TransferJob? _interrupted;

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

                if (_shutdown)
                {
                    // The pane closed while connecting — don't keep a session nobody owns.
                    var orphan = client;
                    _ = Task.Run(() => { try { orphan?.Dispose(); } catch { } });
                    return;
                }
                _cts = new CancellationTokenSource();
                _sftp = client;
                _remoteCwd = _sftp!.WorkingDirectory;
                _connectedStatus = $"Connected — {_cfg.Username}@{_cfg.Host}";
                Emit(ConnectionState.Connected, _connectedStatus);
                _ = AfterConnectAsync();
                return;
            }
            catch (SshAuthenticationException ex)
            {
                client?.Dispose();
                if (_shutdown) return;   // pane closed while connecting — no prompts for it
                if (!UsesPassword(_cfg.Auth) || attempt >= MaxAuthAttempts) { Fail(ex.Message); return; }
                var pw = PromptSecret($"Password for {_cfg.Username}@{_cfg.Host}",
                    $"{_cfg.Host} : {_cfg.Port}", $"Authentication failed: {ex.Message}");
                if (pw == null) { Emit(ConnectionState.Disconnected, "Login cancelled"); return; }
                _cfg.Password = pw;
            }
            catch (KeyPassphraseRequiredException ex)
            {
                client?.Dispose();
                if (_shutdown) return;
                if (attempt >= MaxAuthAttempts) { Fail(ex.Message); return; }
                string? error = string.IsNullOrEmpty(_cfg.KeyPassphrase) ? null : ex.Message;
                var pass = PromptSecret("Passphrase for the private key", _cfg.PrivateKeyPath ?? "", error);
                if (pass == null) { Emit(ConnectionState.Disconnected, "Login cancelled"); return; }
                _cfg.KeyPassphrase = pass;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                if (!_shutdown) Fail(ex.Message);
                return;
            }
        }
    }

    /// <summary>List the remote folder, then carry on with any transfer the previous
    /// connection was cut off in the middle of.</summary>
    private async Task AfterConnectAsync()
    {
        // Wait rather than skip: a listing or operation on the previous client may
        // still hold the gate, and skipping would leave its stale listing on screen.
        await RefreshRemote(waitForGate: true);
        var job = _interrupted;
        if (job == null || _shutdown || _sftp == null || !_sftp.IsConnected) return;
        await RunTransferAsync(job, resumed: true);
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

    /// <summary>An action found no live connection (never connected, or the link died while
    /// idle). Shown as disconnected — not on the green connected strip — unless a connect is
    /// already under way and about to report its own state.</summary>
    private void NotConnected()
    {
        if (_connecting) return;
        Emit(ConnectionState.Disconnected, "Not connected — click ⟳ Reconnect");
    }

    /// <summary>Restore the steady "Connected — user@host" status line.</summary>
    private void StatusIdle() => Emit(ConnectionState.Connected, _connectedStatus);

    /// <summary>Show the last operation's result if one is pending (so a transfer's
    /// completion notice survives the refresh that follows it), else the idle line.</summary>
    private void Settle() => Emit(ConnectionState.Connected, _lastResult ?? _connectedStatus);

    public void FocusDefault() => (_sftp != null ? _remoteList : _localList).Focus();

    public void Shutdown()
    {
        _shutdown = true;
        DisconnectClient();
    }

    private void DisconnectClient()
    {
        var s = _sftp;
        _sftp = null;
        _cts.Cancel();   // a transfer stops at its next chunk and keeps its partial file to resume
        var op = _currentOp;
        if (s != null)
            Task.Run(async () =>
            {
                // Give the operation a moment to stop cleanly, and the server a moment to take
                // a clean disconnect. A server that has stopped answering gets neither: then
                // the socket is cut, which fails the stuck requests so the operation ends.
                if (op != null) { try { await op.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
                bool stuck = op is { IsCompleted: false };
                if (!stuck)
                {
                    var bye = Task.Run(() => { try { s.Disconnect(); } catch { } });
                    stuck = !await bye.WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(t => t.IsCompletedSuccessfully);
                }
                if (stuck) RemoteAuth.AbortSocket(s);
                try { s.Dispose(); } catch { }
            });
    }

    // -------------------- background operation runner --------------------

    /// <summary>Run a blocking SFTP operation off the UI thread, then refresh both panes.
    /// The operation gets the client it started with, so a disconnect can't swap it mid-run.</summary>
    private async Task DoAsync(string busyText, Action<SftpClient, CancellationToken> work,
        bool refreshRemote = true, bool refreshLocal = true, TransferJob? job = null)
    {
        if (_sftp == null || !_sftp.IsConnected) { NotConnected(); return; }
        if (_opRunning) { Status("Busy — wait for the current operation to finish"); return; }
        _opRunning = true;
        try
        {
            await _gate.WaitAsync();   // an auto-refresh listing may still hold it; it's quick
            try
            {
                var sftp = _sftp;
                if (sftp == null || !sftp.IsConnected) { NotConnected(); return; }
                var ct = _cts.Token;
                _lastResult = null;
                if (job != null && ReferenceEquals(job, _interrupted)) _interrupted = null;   // resuming it now
                Status(busyText);
                try
                {
                    var op = Task.Run(() => work(sftp, ct));
                    _currentOp = op;
                    await op;
                    Settle();
                }
                catch (Exception ex) when (job != null && SftpTransfer.IsLinkLoss(ex, sftp))
                {
                    // Keep the job: its files carry on from where they stopped once reconnected.
                    _interrupted = job;
                    // Closing, or Reconnect already replaced the client and will resume the job.
                    if (_shutdown || !ReferenceEquals(sftp, _sftp)) return;
                    Emit(ConnectionState.Disconnected, "Connection lost — transfer paused. Reconnect (⟳) to resume it.");
                    MessageBox.Show(Window.GetWindow(this),
                        $"Connection lost: {ex.Message}\n\nThe transfer is paused and the partial file is kept. " +
                        "Click ⟳ Reconnect to resume it from where it stopped.",
                        "WSCP", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                catch (OperationCanceledException) { /* disconnected mid-operation */ }
                catch (Exception ex)
                {
                    if (_shutdown) return;
                    Status("Error: " + ex.Message);
                    MessageBox.Show(Window.GetWindow(this), ex.Message, "WSCP", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                _currentOp = null;
                _gate.Release();
            }
        }
        finally { _opRunning = false; }

        if (_shutdown) return;
        if (refreshRemote) _ = RefreshRemote();
        if (refreshLocal) RefreshLocal();
    }

    // -------------------- remote listing / navigation --------------------

    private async Task RefreshRemote(bool waitForGate = false)
    {
        // SSH.NET's SftpClient shares one channel and is not safe for concurrent requests,
        // so every remote listing is serialised through _gate — the same lock that guards
        // operations. A plain refresh requested while an op is already running is dropped on
        // purpose: that op refreshes both panes when it finishes.
        if (_sftp == null || !_sftp.IsConnected) { NotConnected(); return; }
        if (waitForGate) await _gate.WaitAsync();
        else if (!_gate.Wait(0)) return;
        try
        {
            var sftp = _sftp;                   // capture: the pane may close and null the field
            if (sftp == null || !sftp.IsConnected) { NotConnected(); return; }
            Status("Refreshing…");
            var cwd = _remoteCwd;
            var list = await Task.Run(() => sftp.ListDirectory(cwd).ToList());
            PopulateRemote(list);
            Settle();
        }
        catch (Exception ex)
        {
            Status("Error: " + ex.Message);
        }
        finally { _gate.Release(); }
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
        await DoAsync($"Opening {path}…", (sftp, _) =>
        {
            sftp.ChangeDirectory(path);
            _remoteCwd = sftp.WorkingDirectory;
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
        await RunTransferAsync(new TransferJob(upload: true, _localCwd, _remoteCwd, items), resumed: false);
    }

    /// <summary>Run (or resume) a transfer. Files the job already finished are skipped and a
    /// file it was part-way through carries on from where it stopped.</summary>
    private Task RunTransferAsync(TransferJob job, bool resumed)
    {
        int n = job.Items.Count;
        string busy = (job.Upload, resumed) switch
        {
            (true, false) => $"Uploading {n} item(s)…",
            (false, false) => $"Downloading {n} item(s)…",
            (true, true) => $"Resuming upload of {n} item(s)…",
            (false, true) => $"Resuming download of {n} item(s)…",
        };
        return DoAsync(busy, (sftp, ct) =>
        {
            for (int i = 0; i < n; i++)
            {
                var it = job.Items[i];
                if (job.Upload)
                    UploadPath(sftp, ct, job, Path.Combine(job.LocalDir, it.Name),
                        CombineRemote(job.RemoteDir, it.Name), i + 1, n);
                else
                    DownloadPath(sftp, ct, job, CombineRemote(job.RemoteDir, it.Name),
                        Path.Combine(job.LocalDir, it.Name), it.IsDirectory, i + 1, n);
            }
            _lastResult = job.Upload
                ? $"✔ Uploaded {n} item(s) to {job.RemoteDir}"
                : $"✔ Downloaded {n} item(s) to {job.LocalDir}";
        }, job: job);
    }

    private void UploadPath(SftpClient sftp, CancellationToken ct, TransferJob job, string localPath,
        string remotePath, int idx, int total)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(localPath))
        {
            EnsureRemoteDir(sftp, remotePath);
            foreach (var d in Directory.GetDirectories(localPath))
                UploadPath(sftp, ct, job, d, CombineRemote(remotePath, Path.GetFileName(d)), idx, total);
            foreach (var f in Directory.GetFiles(localPath))
                UploadPath(sftp, ct, job, f, CombineRemote(remotePath, Path.GetFileName(f)), idx, total);
        }
        else if (!job.Done.Contains(localPath))
        {
            var fi = new FileInfo(localPath);
            string name = fi.Name;
            // Resume only into a remote file this job itself started writing, from a local
            // file that hasn't changed since — anything else is uploaded from the beginning.
            bool unchanged = job.Unchanged(localPath, fi.Length, fi.LastWriteTimeUtc);
            bool resume = unchanged && job.UploadWasStarted(remotePath);
            _progWatch.Reset();                 // first callback of a new file reports at once
            SftpTransfer.Upload(sftp, localPath, remotePath, resume, () => job.MarkUploadStarted(remotePath),
                (done, size) => ReportTransfer("Uploading", name, idx, total, done, size), ct);
            job.Done.Add(localPath);
        }
    }

    private static void EnsureRemoteDir(SftpClient sftp, string path)
    {
        try { if (!sftp.Exists(path)) sftp.CreateDirectory(path); }
        catch { /* may already exist */ }
    }

    private async void DownloadSelected()
    {
        var items = SelectedRemote();
        if (items.Count == 0) { Status("Select remote files to download"); return; }
        await RunTransferAsync(new TransferJob(upload: false, _localCwd, _remoteCwd, items), resumed: false);
    }

    private void DownloadPath(SftpClient sftp, CancellationToken ct, TransferJob job, string remotePath,
        string localPath, bool isDir, int idx, int total)
    {
        ct.ThrowIfCancellationRequested();
        if (isDir)
        {
            Directory.CreateDirectory(localPath);
            foreach (var f in sftp.ListDirectory(remotePath))
            {
                if (f.Name is "." or "..") continue;
                DownloadPath(sftp, ct, job, CombineRemote(remotePath, f.Name), Path.Combine(localPath, f.Name),
                    f.IsDirectory, idx, total);
            }
        }
        else if (!job.Done.Contains(remotePath))
        {
            string name = Path.GetFileName(localPath);
            // Carry on from an existing .part unless the remote file changed since this job
            // first saw it (then the partial copy is of something else and starts over).
            bool resume = true;
            try
            {
                var a = sftp.GetAttributes(remotePath);
                resume = job.Unchanged(remotePath, a.Size, a.LastWriteTimeUtc);
            }
            catch (SftpException) { /* stat refused — opening the file reports any real problem */ }
            _progWatch.Reset();                 // first callback of a new file reports at once
            SftpTransfer.Download(sftp, remotePath, localPath, resume,
                (done, size) => ReportTransfer("Downloading", name, idx, total, done, size), ct);
            job.Done.Add(remotePath);
        }
    }

    /// <summary>Throttled per-file transfer progress on the status strip. Called from the
    /// transfer thread on each buffer; limited to a few updates a second so the
    /// dispatcher isn't flooded, but always fires once the file is complete.</summary>
    private void ReportTransfer(string verb, string name, int idx, int total, long done, long size)
    {
        bool finished = size <= 0 || done >= size;
        if (_progWatch.IsRunning && _progWatch.ElapsedMilliseconds < 120 && !finished) return;
        _progWatch.Restart();
        string amount = size > 0
            ? $"{done * 100 / size}%  ({FmtBytes(done)} / {FmtBytes(size)})"
            : FmtBytes(done);
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
            await DoAsync($"Creating {name}…", (sftp, _) => sftp.CreateDirectory(CombineRemote(_remoteCwd, name)), refreshLocal: false);
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
                (sftp, _) => sftp.RenameFile(CombineRemote(_remoteCwd, it.Name), CombineRemote(_remoteCwd, name)),
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
            await DoAsync($"Deleting {items.Count} item(s)…", (sftp, ct) =>
            {
                foreach (var it in items)
                {
                    ct.ThrowIfCancellationRequested();
                    DeleteRemote(sftp, CombineRemote(_remoteCwd, it.Name), it.IsDirectory);
                }
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

    private static void DeleteRemote(SftpClient sftp, string path, bool isDir)
    {
        if (isDir)
        {
            foreach (var f in sftp.ListDirectory(path))
            {
                if (f.Name is "." or "..") continue;
                DeleteRemote(sftp, CombineRemote(path, f.Name), f.IsDirectory);
            }
            sftp.DeleteDirectory(path);
        }
        else sftp.DeleteFile(path);
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
            (_, _) => { if (remote) _ = RefreshRemote(); else RefreshLocal(); }));
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

    // -------------------- transfer job --------------------

    /// <summary>One upload or download of a selection, kept so it can be resumed after the
    /// link drops: the folders it ran between, the items, and how far it got.</summary>
    private sealed class TransferJob
    {
        public bool Upload { get; }
        public string LocalDir { get; }
        public string RemoteDir { get; }
        public List<FsItem> Items { get; }

        /// <summary>Source files transferred in full — skipped when the job resumes.</summary>
        public HashSet<string> Done { get; } = new(StringComparer.Ordinal);

        /// <summary>Remote files the server has accepted upload data for in this job, whose
        /// contents are therefore its own and safe to carry on from. Marked from SSH.NET's
        /// progress thread, hence the lock.</summary>
        private readonly HashSet<string> _uploadStarted = new(StringComparer.Ordinal);

        public void MarkUploadStarted(string remotePath) { lock (_uploadStarted) _uploadStarted.Add(remotePath); }

        public bool UploadWasStarted(string remotePath) { lock (_uploadStarted) return _uploadStarted.Contains(remotePath); }

        private readonly Dictionary<string, (long Size, DateTime Modified)> _seen = new(StringComparer.Ordinal);

        public TransferJob(bool upload, string localDir, string remoteDir, List<FsItem> items)
        {
            Upload = upload;
            LocalDir = localDir;
            RemoteDir = remoteDir;
            Items = items;
        }

        /// <summary>Records a source file's size and timestamp the first time the job sees it;
        /// later calls report whether it is still the same file.</summary>
        public bool Unchanged(string path, long size, DateTime modified)
        {
            if (_seen.TryGetValue(path, out var was)) return was == (size, modified);
            _seen[path] = (size, modified);
            return true;
        }
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
