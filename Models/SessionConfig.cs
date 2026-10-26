using System.Text.Json.Serialization;

namespace MultiSSH.Models;

public enum AuthMethod
{
    Password,
    PublicKey,
    Agent,
    KeyboardInteractive
}

/// <summary>What a session actually talks to.</summary>
public enum SessionKind
{
    /// <summary>A remote SSH host (the default, and how every pre-existing session loads).</summary>
    Ssh,
    /// <summary>A local Windows PowerShell console on this machine.</summary>
    PowerShell,
    /// <summary>A local cmd.exe console on this machine.</summary>
    Cmd,
    /// <summary>A local bash console on this machine (Git for Windows).</summary>
    Bash,
    /// <summary>A local WSL shell (wsl.exe — the default distribution).</summary>
    Wsl,
    /// <summary>An interactive SFTP file-transfer session over SSH.</summary>
    Sftp,
    /// <summary>An interactive SCP file-copy session over SSH.</summary>
    Scp,
    /// <summary>An interactive WebDAV file session over HTTP(S).</summary>
    WebDav,
    /// <summary>A WinSCP-style graphical dual-pane SFTP file manager (local ↔ remote).</summary>
    Wscp
}

/// <summary>
/// A saved / active session profile. Mirrors the common set of options
/// exposed by PuTTY's configuration dialog.
/// </summary>
public class SessionConfig
{
    // --- Session ---

    /// <summary>SSH, or a local shell. Old saved sessions have no value and load as SSH.</summary>
    public SessionKind Kind { get; set; } = SessionKind.Ssh;

    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";

    /// <summary>Folder this connection lives in, e.g. "Client A/Prod". "" = top level.
    /// Uses '/' as the separator. Old sessions load with "" (root), so nothing is lost.</summary>
    public string FolderPath { get; set; } = "";

    /// <summary>Manual position within its folder, set when the user drags to reorder.
    /// null = unordered, so it sorts alphanumerically by display name (the default).</summary>
    public int? SortOrder { get; set; }

    // --- Connection / Authentication ---
    public AuthMethod Auth { get; set; } = AuthMethod.Password;

    /// <summary>Optional password. Stored obfuscated on disk (not secure — see note in SessionStore).</summary>
    public string? Password { get; set; }

    /// <summary>Path to a private key file (OpenSSH or PuTTY .ppk converted).</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Optional passphrase for the private key.</summary>
    public string? KeyPassphrase { get; set; }

    // --- Connection tuning ---
    public int KeepAliveSeconds { get; set; } = 30;
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public bool TcpNoDelay { get; set; } = true;
    public bool SoKeepalive { get; set; } = false;
    public string IpVersion { get; set; } = "Auto";

    // --- Terminal ---
    public string TerminalType { get; set; } = "xterm-256color";
    public int Columns { get; set; } = 80;
    public int Rows { get; set; } = 24;

    // --- Appearance ---
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 14;
    public string ColorScheme { get; set; } = "Campbell";
    public int ScrollbackLines { get; set; } = 2000;

    // --- Behaviour ---
    public bool BellEnabled { get; set; } = true;
    /// <summary>PuTTY-style: selecting text copies, right-click pastes.</summary>
    public bool CopyOnSelect { get; set; } = true;
    public bool PasteOnRightClick { get; set; } = true;

    /// <summary>PowerShell / CMD only: start the shell elevated (UAC prompt on open).</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>Kinds that can be started as Administrator.</summary>
    public static bool CanRunAsAdmin(SessionKind k) => k is SessionKind.PowerShell or SessionKind.Cmd;

    /// <summary>True when this session should actually start elevated.</summary>
    [JsonIgnore]
    public bool Elevated => RunAsAdmin && CanRunAsAdmin(Kind);

    /// <summary>True for a shell running on this machine rather than a remote connection.</summary>
    [JsonIgnore]
    public bool IsLocal => IsLocalKind(Kind);

    /// <summary>Whether a kind runs locally (vs. a remote SSH/SFTP/SCP/WebDAV connection).</summary>
    public static bool IsLocalKind(SessionKind k) =>
        k is SessionKind.PowerShell or SessionKind.Cmd or SessionKind.Bash or SessionKind.Wsl;

    /// <summary>Friendly name of the local shell, e.g. "PowerShell".</summary>
    [JsonIgnore]
    public string LocalShellName => KindName(Kind);

    /// <summary>Display name for a session kind.</summary>
    public static string KindName(SessionKind kind) => kind switch
    {
        SessionKind.Cmd => "CMD",
        SessionKind.Bash => "Bash",
        SessionKind.Wsl => "WSL",
        SessionKind.PowerShell => "PowerShell",
        SessionKind.Sftp => "SFTP",
        SessionKind.Scp => "SCP",
        SessionKind.WebDav => "WebDAV",
        SessionKind.Wscp => "WSCP",
        _ => "SSH",
    };

    /// <summary>Short type badge shown in headers (matches the config dialog icons).</summary>
    public static string KindBadge(SessionKind kind) => kind switch
    {
        SessionKind.PowerShell => "PS",
        SessionKind.Cmd => ">_",
        SessionKind.Bash => "$",
        SessionKind.Wsl => "🐧",
        _ => "SSH",
    };

    [JsonIgnore]
    public string KindBadgeText => KindBadge(Kind);

    [JsonIgnore]
    public string Display => !string.IsNullOrWhiteSpace(Name) ? Name + (Elevated ? " (Admin)" : "")
        : IsLocal ? LocalShellName + (Elevated ? " (Admin)" : "")
        : string.IsNullOrWhiteSpace(Host) ? "(new session)"
        : $"{Username}@{Host}";

    public SessionConfig Clone()
    {
        return (SessionConfig)MemberwiseClone();
    }
}
