using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiSSH.Services;

/// <summary>
/// Records a session's output to a plain-text transcript file. ANSI escape
/// sequences and control bytes are stripped so the log reads like a clean
/// transcript — handy for turning a session into a script.
/// </summary>
public class Recorder : IDisposable
{
    private StreamWriter? _writer;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private string _pending = "";
    private readonly object _lock = new();

    public bool IsActive => _writer != null;
    public string? FilePath { get; private set; }

    /// <summary>Default folder recordings are written to.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Multi-SSH Recordings");

    public void Start(string folder, string sessionName)
    {
        lock (_lock)
        {
            if (_writer != null) return;
            Directory.CreateDirectory(folder);

            var safe = Sanitize(string.IsNullOrWhiteSpace(sessionName) ? "session" : sessionName);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(folder, $"{safe}_{stamp}.log");
            int n = 2;
            while (File.Exists(path))
                path = Path.Combine(folder, $"{safe}_{stamp}_{n++}.log");

            FilePath = path;
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            _writer.WriteLine($"# Multi-SSH recording — {sessionName} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine();
        }
    }

    public void Write(byte[] data)
    {
        lock (_lock)
        {
            if (_writer == null || data.Length == 0) return;

            try
            {
                var chars = new char[data.Length * 2];
                int n = _decoder.GetChars(data, 0, data.Length, chars, 0);
                _pending += new string(chars, 0, n);

                // Hold back a trailing, still-incomplete escape sequence until more arrives.
                int cut = _pending.Length;
                int esc = _pending.LastIndexOf('\x1b');
                if (esc >= 0 && !CompleteEscapeAtStart(_pending.Substring(esc)))
                    cut = esc;

                var chunk = _pending.Substring(0, cut);
                _pending = _pending.Substring(cut);

                _writer.Write(AnsiStripper.Clean(chunk));
            }
            catch
            {
                // Disk full, or the recordings folder (e.g. a network share) went away. This
                // runs on the connection's read thread, so throwing would end the session's
                // output — stop recording instead and keep the session alive.
                CloseWriter();
            }
        }
    }

    public void Stop()
    {
        lock (_lock) CloseWriter();
    }

    private void CloseWriter()
    {
        if (_writer == null) return;
        try { _writer.Write(AnsiStripper.Clean(_pending)); _writer.Flush(); _writer.Dispose(); }
        catch { /* best effort */ }
        _writer = null;
        _pending = "";
    }

    public void Dispose() => Stop();

    // Complete forms: CSI, OSC, a charset designator (ESC ( B), or any other two-byte
    // escape (ESC 7, ESC 8, ESC =, ESC >, ESC M …). Missing one of these made the recorder
    // hold every later byte back until another escape arrived.
    private static readonly Regex EscAtStart = new(
        @"^\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[()*+#].|[0-~-[\[\]]])",
        RegexOptions.Compiled);

    private static bool CompleteEscapeAtStart(string tail) => EscAtStart.IsMatch(tail);

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        s = s.Replace(' ', '_').Trim('_');
        return s.Length > 60 ? s.Substring(0, 60) : s;
    }
}

/// <summary>Strips ANSI/VT escape sequences and stray control bytes from text.</summary>
public static class AnsiStripper
{
    private static readonly Regex Osc = new(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.Compiled);
    private static readonly Regex Csi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex OtherEsc = new(@"\x1B(?:[()*+#].|[0-~])", RegexOptions.Compiled);

    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = Osc.Replace(s, "");
        s = Csi.Replace(s, "");
        s = OtherEsc.Replace(s, "");

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\n' || c == '\t') sb.Append(c);
            else if (c == '\r') { /* drop; CRLF -> LF */ }
            else if (c == '\b') { if (sb.Length > 0 && sb[^1] != '\n') sb.Length--; } // handle backspace
            else if (c >= ' ') sb.Append(c);
            // other control chars dropped
        }
        return sb.ToString();
    }
}
