using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiSSH.Models;
using MultiSSH.Services;
using MultiSSH.Terminal;
using Renci.SshNet.Common;

namespace MultiSSH.Views;

/// <summary>Live connection state, surfaced as a coloured dot in pane/tab headers.</summary>
public enum ConnectionState { Idle, Connecting, Connected, Disconnected, Failed }

/// <summary>
/// One live SSH session: a terminal plus a status strip, backed by an
/// <see cref="SshConnection"/>. Used interchangeably inside tabs or tiles.
/// </summary>
public class SessionView : Grid
{
    private readonly SessionConfig _cfg;
    /// <summary>The terminal body — null for a WSCP file-manager session (see <see cref="_wscp"/>).</summary>
    private readonly TerminalControl? _term;
    /// <summary>The WinSCP-style file-manager body — non-null only for a WSCP session.</summary>
    private readonly WscpPanel? _wscp;
    private readonly TextBlock _status;
    private readonly TextBlock _position;
    private readonly Border _statusBar;
    private volatile ITerminalBackend? _conn;   // read by backend threads to drop stale events
    private bool _connected;
    private bool _pendingConnect = true;
    private bool _connecting;   // guards against overlapping connect/reconnect attempts
    private bool _closed;
    // Written on the UI thread (via SetState's Dispatcher call) but read from backend
    // read/wait threads in the Closed handler, so mark it volatile to avoid a stale read.
    private volatile ConnectionState _state = ConnectionState.Idle;
    private readonly Recorder _recorder = new();

    public SessionConfig Config => _cfg;
    public string TabTitle { get; private set; }
    public ConnectionState State => _state;
    public string StatusText { get; private set; } = "Idle";

    public event Action<SessionView>? TitleChanged;
    public event Action<SessionView>? ConnectionClosed;
    /// <summary>Raised when the remote shell exits — the host destroys the pane.</summary>
    public event Action<SessionView>? ShellExited;
    /// <summary>Raised when connection state or status text changes (UI thread).</summary>
    public event Action<SessionView>? StateChanged;

    /// <summary>Header status-dot colour for a given state.</summary>
    public static Color DotColor(ConnectionState s) => s switch
    {
        ConnectionState.Connected => Color.FromRgb(0x16, 0xC6, 0x0C),
        ConnectionState.Connecting => Color.FromRgb(0xE0, 0xA0, 0x30),
        ConnectionState.Failed => Color.FromRgb(0xE7, 0x48, 0x56),
        ConnectionState.Disconnected => Color.FromRgb(0x99, 0x99, 0x99),
        _ => Color.FromRgb(0x77, 0x77, 0x77),
    };

    /// <summary>Connection-type colour (used to tint the type glyph).</summary>
    public static Color KindColor(SessionKind k) => k switch
    {
        SessionKind.PowerShell => Color.FromRgb(0x24, 0x72, 0xC8), // blue
        SessionKind.Cmd => Color.FromRgb(0x80, 0x80, 0x80),        // gray
        SessionKind.Bash => Color.FromRgb(0x4E, 0xAA, 0x25),       // green
        SessionKind.Wsl => Color.FromRgb(0xE9, 0x54, 0x20),        // ubuntu orange
        SessionKind.Sftp => Color.FromRgb(0x17, 0xA2, 0xB8),       // teal
        SessionKind.Scp => Color.FromRgb(0x6D, 0x28, 0xD9),        // violet
        SessionKind.WebDav => Color.FromRgb(0xB4, 0x53, 0x09),     // amber/brown
        SessionKind.Wscp => Color.FromRgb(0x2E, 0x7D, 0x32),       // WinSCP green
        _ => Color.FromRgb(0x3A, 0x96, 0xDD),                       // SSH: cyan
    };

    /// <summary>Connection-type icon glyph — the original per-type icons
    /// (PowerShell = 🌐 globe), coloured via <see cref="KindColor"/>.</summary>
    public static string KindGlyph(SessionKind k) => k switch
    {
        SessionKind.PowerShell => "🌐",
        SessionKind.Cmd => ">_",
        SessionKind.Bash => "$",
        SessionKind.Wsl => "🐧",
        SessionKind.Sftp => "⇅",
        SessionKind.Scp => "⇄",
        SessionKind.WebDav => "☁",
        SessionKind.Wscp => "🗂",
        _ => "SSH",
    };

    private static Color BarColor(ConnectionState s) => s switch
    {
        ConnectionState.Connected => Color.FromRgb(0x1e, 0x3a, 0x1e),
        ConnectionState.Connecting => Color.FromRgb(0x3a, 0x33, 0x1e),
        ConnectionState.Failed => Color.FromRgb(0x3a, 0x1e, 0x1e),
        _ => Color.FromRgb(0x25, 0x25, 0x2b),
    };

    /// <summary>
    /// Colour the status strip. Active states (connected/connecting/failed) keep their
    /// tinted bars with white text on both themes; the neutral idle/disconnected state
    /// follows the app chrome theme so it isn't a dark strip on a light UI.
    /// </summary>
    private void ApplyStatusTheme(ConnectionState state)
    {
        bool neutral = state is ConnectionState.Idle or ConnectionState.Disconnected;
        if (neutral)
        {
            _statusBar.SetResourceReference(Border.BackgroundProperty, ThemeManager.HeaderBg);
            _status.SetResourceReference(TextBlock.ForegroundProperty, ThemeManager.Text);
            _position.SetResourceReference(TextBlock.ForegroundProperty, ThemeManager.MutedText);
        }
        else
        {
            _statusBar.Background = new SolidColorBrush(BarColor(state));
            _status.Foreground = Brushes.White;
            _position.Foreground = Brushes.White;
        }
    }

    /// <summary>Set the "x of y" open-window counter shown at the bottom of the frame.
    /// Hidden entirely when disabled in Settings.</summary>
    public void SetPosition(int index, int total)
    {
        _position.Text = $"{index} of {total}";
        _position.Visibility = AppSettings.Current.ShowWindowCounter ? Visibility.Visible : Visibility.Collapsed;
    }

    public SessionView(SessionConfig cfg)
    {
        _cfg = cfg;
        TabTitle = cfg.Display;

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (cfg.Kind == SessionKind.Wscp)
        {
            // A WSCP session has no terminal — it hosts a graphical dual-pane file
            // manager whose connection state feeds the same status strip.
            _wscp = new WscpPanel(cfg);
            _wscp.StateChanged += (state, text) => SetState(state, text);
            SetRow(_wscp, 0);
            Children.Add(_wscp);
        }
        else
        {
            _term = new TerminalControl(cfg);
            _term.Input += bytes => _conn?.Send(bytes);
            _term.GridResized += (cols, rows) => _conn?.Resize(cols, rows);
            _term.TitleChanged += t =>
            {
                // Local shells (PowerShell/CMD/Bash/WSL) set their OSC window title to the
                // current directory path; show the shell name instead. Remote sessions keep
                // the OSC title so it can reflect the remote host/app.
                var next = cfg.IsLocal || string.IsNullOrWhiteSpace(t) ? cfg.Display : t;
                if (next == TabTitle) return;
                TabTitle = next;
                TitleChanged?.Invoke(this);
            };
            SetRow(_term, 0);
            Children.Add(_term);
            AddTerminalMenuItems();
        }

        _status = new TextBlock
        {
            Text = "Idle",
            Margin = new Thickness(6, 2, 6, 2),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // "x of y" counter: this window's position and the total number of open windows.
        _position = new TextBlock
        {
            Margin = new Thickness(8, 2, 6, 2),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var statusDock = new DockPanel();
        DockPanel.SetDock(_position, Dock.Left);
        statusDock.Children.Add(_position);   // far left edge
        statusDock.Children.Add(_status);     // fills the remainder
        _statusBar = new Border { Child = statusDock };
        ApplyStatusTheme(ConnectionState.Idle);
        SetRow(_statusBar, 1);
        Children.Add(_statusBar);

        Loaded += OnLoaded;
        Unloaded += (_, _) => { /* keep connection alive across tab/tile moves */ };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_pendingConnect)
        {
            _pendingConnect = false;
            _ = ConnectAsync();
        }
        FocusTerminal();
    }

    private const int MaxAuthAttempts = 4;

    public async Task ConnectAsync()
    {
        if (_connecting) return;   // a connect attempt is already in flight
        _connecting = true;
        try { await ConnectCoreAsync(); }
        finally { _connecting = false; }
    }

    private async Task ConnectCoreAsync()
    {
        if (_cfg.Kind == SessionKind.Wscp)
        {
            await _wscp!.ConnectAsync();
            return;
        }

        int cols = _term!.Buffer.Cols;
        int rows = _term.Buffer.Rows;

        if (_cfg.IsLocal)
        {
            await StartLocalShellAsync(cols, rows);
            return;
        }

        SetState(ConnectionState.Connecting, "Connecting…");

        // If this method needs a password and none was saved, ask for one now
        // (securely, masked) rather than failing the login. A key file means
        // public-key auth, so never prompt for a password in that case.
        if (UsesPassword(_cfg.Auth) && string.IsNullOrEmpty(_cfg.Password)
            && string.IsNullOrEmpty(_cfg.PrivateKeyPath))
        {
            var pw = PromptSecret($"Password for {_cfg.Username}@{_cfg.Host}",
                $"Host {_cfg.Host} : {_cfg.Port}", null);
            if (pw == null) { SetState(ConnectionState.Disconnected, "Login cancelled — no password entered"); return; }
            _cfg.Password = pw;
        }

        for (int attempt = 1; ; attempt++)
        {
            var backend = CreateRemoteBackend();
            HookBackend(backend);
            _conn = backend;

            try
            {
                await backend.ConnectAsync(cols, rows);
                if (_closed) return;
                // The pane may have been resized while connecting (layout rebuilt, password
                // prompt open); those resizes were dropped because there was no PTY yet.
                if (_term!.Buffer.Cols != cols || _term.Buffer.Rows != rows)
                    backend.Resize(_term.Buffer.Cols, _term.Buffer.Rows);
                SetState(ConnectionState.Connected, $"Connected — {_cfg.Username}@{_cfg.Host}");
                _term.Feed(Enc($"\x1b[32m*** Connected to {_cfg.Host} ***\x1b[0m\r\n"));
                return;
            }
            catch (SshAuthenticationException ex)
            {
                DropBackend(backend);
                if (_closed) return;

                // Re-prompt on rejection (wrong or empty password), up to a limit.
                if (!UsesPassword(_cfg.Auth) || attempt >= MaxAuthAttempts)
                {
                    Fail(ex.Message);
                    return;
                }

                var pw = PromptSecret($"Password for {_cfg.Username}@{_cfg.Host}",
                    $"Host {_cfg.Host} : {_cfg.Port}", $"Authentication failed: {ex.Message}");
                if (pw == null) { SetState(ConnectionState.Disconnected, "Login cancelled"); return; }
                _cfg.Password = pw;
            }
            catch (KeyPassphraseRequiredException ex)
            {
                DropBackend(backend);
                if (_closed) return;

                if (attempt >= MaxAuthAttempts) { Fail(ex.Message); return; }

                // Show the error only after a wrong attempt (a passphrase was set).
                string? error = string.IsNullOrEmpty(_cfg.KeyPassphrase) ? null : ex.Message;
                var pass = PromptSecret("Passphrase for the private key",
                    _cfg.PrivateKeyPath ?? "", error);
                if (pass == null) { SetState(ConnectionState.Disconnected, "Login cancelled"); return; }
                _cfg.KeyPassphrase = pass;
            }
            catch (Exception ex)
            {
                DropBackend(backend);
                if (!_closed) Fail(ex.Message);
                return;
            }
        }
    }

    /// <summary>Dispose a backend whose connect attempt failed, and forget it if it's current.</summary>
    private void DropBackend(ITerminalBackend backend)
    {
        backend.Dispose();
        if (ReferenceEquals(_conn, backend)) _conn = null;
    }

    /// <summary>
    /// Start a local PowerShell / cmd session. No host, no credentials — the
    /// shell either launches or it doesn't.
    /// </summary>
    private async Task StartLocalShellAsync(int cols, int rows)
    {
        SetState(ConnectionState.Connecting, $"Starting {_cfg.LocalShellName} …");

        var local = new LocalShellConnection(_cfg);
        HookBackend(local);
        _conn = local;

        try
        {
            await local.ConnectAsync(cols, rows);
            SetState(ConnectionState.Connected, $"{_cfg.LocalShellName} — local");
        }
        catch (Exception ex)
        {
            DropBackend(local);
            if (!_closed) Fail(ex.Message);
        }
    }

    /// <summary>Create the right remote backend for the session kind.</summary>
    private ITerminalBackend CreateRemoteBackend() => _cfg.Kind switch
    {
        SessionKind.Sftp => new SftpConnection(_cfg),
        SessionKind.Scp => new ScpConnection(_cfg),
        SessionKind.WebDav => new WebDavConnection(_cfg),
        _ => new SshConnection(_cfg),
    };

    /// <summary>Wire a backend's events to this view (shared by SSH and local shells).</summary>
    private void HookBackend(ITerminalBackend backend)
    {
        // Events from a backend that has been replaced (reconnect) or dropped (close) are
        // ignored: its teardown runs in the background and must not overwrite the state
        // of the connection that replaced it.
        bool Current() => ReferenceEquals(_conn, backend);

        backend.StatusChanged += s => { if (Current()) SetStatusText(s); };
        backend.DataReceived += bytes =>
        {
            if (!Current()) return;
            _term!.Feed(bytes);
            _recorder.Write(bytes);
        };
        backend.Closed += msg =>
        {
            if (!Current()) return;
            if (_state != ConnectionState.Connecting)
                SetState(ConnectionState.Disconnected, msg);
            Dispatcher.BeginInvoke(() => ConnectionClosed?.Invoke(this));
        };
        backend.ShellExited += () =>
        {
            if (!Current()) return;
            SetState(ConnectionState.Disconnected, "Shell closed");
            Dispatcher.BeginInvoke(() => ShellExited?.Invoke(this));
        };
    }

    private static bool UsesPassword(AuthMethod m)
        => m is AuthMethod.Password or AuthMethod.KeyboardInteractive or AuthMethod.Agent;

    /// <summary>Show a masked secret prompt (password or passphrase). Null if cancelled.</summary>
    private string? PromptSecret(string prompt, string target, string? error)
    {
        var dlg = new PasswordPromptDialog(prompt, target, error)
        {
            Owner = Window.GetWindow(this),
        };
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    private void Fail(string msg)
    {
        SetState(ConnectionState.Failed, "Connection failed: " + msg);
        _term?.Feed(Enc($"\r\n\x1b[31m*** Connection failed: {msg} ***\x1b[0m\r\n"));
    }

    private static byte[] Enc(string s) => Encoding.UTF8.GetBytes(s);

    public async Task ReconnectAsync()
    {
        if (_connecting) return;   // don't interrupt/overwrite an in-flight connect
        if (_cfg.Kind == SessionKind.Wscp)
        {
            SetState(ConnectionState.Connecting, "Reconnecting …");
            await _wscp!.ReconnectAsync();
            return;
        }
        DisposeInBackground();
        SetState(ConnectionState.Connecting, "Reconnecting …");
        await ConnectAsync();
        _term?.Focus();
    }

    /// <summary>Detach the current backend and tear it down off the UI thread, so a
    /// wedged socket or a slow ConPTY shutdown can't freeze the app.</summary>
    private void DisposeInBackground()
    {
        var conn = _conn;
        _conn = null;
        if (conn != null)
            Task.Run(() => { try { conn.Dispose(); } catch { /* best effort */ } });
    }

    /// <summary>Change connection state (dot colour + status bar) and status text.
    /// State fields are written on the UI thread so backend threads never race the UI.</summary>
    private void SetState(ConnectionState state, string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _state = state;
            _connected = state == ConnectionState.Connected;
            StatusText = text;
            _status.Text = text;
            ApplyStatusTheme(state);
            StateChanged?.Invoke(this);
        });
    }

    /// <summary>Update the status text only, keeping the current state/colour.</summary>
    private void SetStatusText(string s)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText = s;
            _status.Text = s;
            StateChanged?.Invoke(this);
        });
    }

    public bool IsConnected => _connected;

    public void FocusTerminal()
    {
        if (_wscp != null) _wscp.FocusDefault();
        else _term?.Focus();
    }

    /// <summary>True for a WSCP file-manager session (no terminal, no broadcast target).</summary>
    public bool IsFileManager => _wscp != null;

    /// <summary>Send raw text to the remote shell (used by the broadcast bar). No-op for WSCP.</summary>
    public void SendText(string text) => _conn?.Send(text);

    // -------------------- output recording --------------------

    public bool IsRecording => _recorder.IsActive;
    public string? RecordingFile => _recorder.FilePath;

    /// <summary>Start writing this session's output to a transcript file in <paramref name="folder"/>.</summary>
    public void StartRecording(string folder) => _recorder.Start(folder, TabTitle);

    public void StopRecording() => _recorder.Stop();

    private MenuItem? _recordMenuItem;

    /// <summary>Add "Record" and "Open recordings folder" to the terminal's right-click menu.</summary>
    private void AddTerminalMenuItems()
    {
        if (_term?.ContextMenu is not ContextMenu menu) return;
        menu.Items.Add(new Separator());
        _recordMenuItem = new MenuItem { Header = "Start Recording (this session)" };
        _recordMenuItem.Click += (_, _) => ToggleRecording();
        menu.Items.Add(_recordMenuItem);
        var openFolder = new MenuItem { Header = "Open Recordings Folder…" };
        openFolder.Click += (_, _) => OpenRecordingsFolder();
        menu.Items.Add(openFolder);
        menu.Opened += (_, _) =>
            _recordMenuItem.Header = IsRecording ? "Stop Recording (this session)" : "Start Recording (this session)";
    }

    private void ToggleRecording()
    {
        if (IsRecording) StopRecording();
        else StartRecording(AppSettings.Current.EffectiveRecordingsFolder);
    }

    private void OpenRecordingsFolder()
    {
        try
        {
            var folder = AppSettings.Current.EffectiveRecordingsFolder;
            System.IO.Directory.CreateDirectory(folder);
            if (!string.IsNullOrEmpty(RecordingFile) && System.IO.File.Exists(RecordingFile))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{RecordingFile}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch { /* explorer unavailable */ }
    }

    /// <summary>Stop the UI timers now and tear the connection down in the background.</summary>
    public void Close()
    {
        _closed = true;
        _recorder.Stop();
        _term?.Shutdown();
        _wscp?.Shutdown();
        DisposeInBackground();
    }
}
