using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiSSH.Models;

namespace MultiSSH.Services;

/// <summary>
/// Application-wide preferences (as opposed to per-session settings), stored in
/// %AppData%\Multi-SSH\settings.json. Currently holds the defaults applied to
/// each new session — most importantly the default terminal font.
/// </summary>
public class AppSettings
{
    public string DefaultFontFamily { get; set; } = "Consolas";
    public double DefaultFontSize { get; set; } = 14;
    public string DefaultColorScheme { get; set; } = "Campbell";

    /// <summary>App chrome theme: "Dark" (default) or "Light".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Show the "x of y" open-window counter at the bottom of each session.</summary>
    public bool ShowWindowCounter { get; set; } = true;

    // ---- Window (PuTTY-style) ----

    /// <summary>Default initial terminal size for new sessions.</summary>
    public int DefaultColumns { get; set; } = 80;
    public int DefaultRows { get; set; } = 24;
    public int DefaultScrollbackLines { get; set; } = 2000;

    /// <summary>How the terminal reacts to the pane being resized:
    /// "RowsCols" (reflow), "FontSize" (scale font, keep rows/cols),
    /// "FontSizeMax" (scale font only when the window is maximized), "Forbid".</summary>
    public string ResizeBehavior { get; set; } = "RowsCols";

    public bool DisplayScrollbar { get; set; } = true;
    public bool ScrollbarInFullScreen { get; set; }
    public bool ResetScrollbackOnKeypress { get; set; }
    public bool ResetScrollbackOnActivity { get; set; } = true;
    public bool PushErasedToScrollback { get; set; } = true;

    /// <summary>User-configured hot keys (key → command).</summary>
    public List<HotKey> HotKeys { get; set; } = new();

    /// <summary>Session-manager folder paths (e.g. "Client A", "Client A/Prod"),
    /// including empty folders so structure persists even with no connections in it.</summary>
    public List<string> SessionFolders { get; set; } = new();

    /// <summary>Folder where session recordings (transcripts) are written.
    /// Empty = the default (Documents\Multi-SSH Recordings).</summary>
    public string RecordingsFolder { get; set; } = "";

    /// <summary>Resolved recordings folder (falls back to the default when unset).</summary>
    [JsonIgnore]
    public string EffectiveRecordingsFolder =>
        string.IsNullOrWhiteSpace(RecordingsFolder) ? Recorder.DefaultFolder : RecordingsFolder;

    // ---- Connection tuning defaults ----

    /// <summary>Seconds between null keepalive packets (0 = disabled).</summary>
    public int DefaultKeepAliveSeconds { get; set; } = 0;

    /// <summary>Default for TCP_NODELAY (disable Nagle's algorithm).</summary>
    public bool DefaultTcpNoDelay { get; set; } = true;

    /// <summary>Default for SO_KEEPALIVE (OS-level TCP keepalives).</summary>
    public bool DefaultSoKeepalive { get; set; } = false;

    /// <summary>Preferred IP version for new sessions: "Auto", "IPv4", or "IPv6".</summary>
    public string DefaultIpVersion { get; set; } = "Auto";

    /// <summary>Which edge of the terminal area the session manager is docked to:
    /// "Left", "Right" or "Bottom".</summary>
    public string SessionManagerDock { get; set; } = "Left";

    /// <summary>True when the session manager is minimized to its edge tab.</summary>
    public bool SessionManagerCollapsed { get; set; }

    /// <summary>Panel size when docked to a side (Left/Right).</summary>
    public double SessionManagerWidth { get; set; } = 250;

    /// <summary>Panel size when docked to the bottom.</summary>
    public double SessionManagerHeight { get; set; } = 200;

    // ---- Main window placement ----
    // Saved continuously (not just on exit) so an abnormal termination still
    // reopens where the window was. Null = never saved: use the XAML default,
    // centred on screen. Values are WPF device-independent units, matching
    // Window.Left/Top/Width/Height and SystemParameters.VirtualScreen*.

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>True when the window was maximized; the Window* values above then
    /// hold its restore (un-maximized) bounds.</summary>
    public bool WindowMaximized { get; set; }

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Multi-SSH");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // The file exists but couldn't be parsed (e.g. truncated by a crash mid-write).
            // Preserve it rather than silently discarding the user's folder tree / hotkeys,
            // so it can be recovered, then start from defaults.
            try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); }
            catch { /* best-effort */ }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            // Atomic write: serialize to a temp file, then swap it in with File.Replace so
            // a crash or power loss mid-write can never leave a truncated settings.json
            // (which would reset the session-manager folder tree and all preferences).
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, FilePath + ".bak");
            else
                File.Move(tmp, FilePath);

            _current = this;
        }
        catch { /* ignore write errors */ }
    }
}
