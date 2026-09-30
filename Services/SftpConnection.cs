using System.IO;
using MultiSSH.Models;
using Renci.SshNet;

namespace MultiSSH.Services;

/// <summary>An interactive SFTP session (ls/cd/get/put/…) over SSH via SSH.NET.</summary>
public class SftpConnection : InteractivePromptBackend
{
    private SftpClient? _sftp;
    private string _cwd = "/";

    public SftpConnection(SessionConfig cfg) : base(cfg) { }

    protected override string PromptText => $"sftp {_cwd}> ";

    protected override void ConnectClient()
    {
        _sftp = new SftpClient(RemoteAuth.BuildConnectionInfo(Cfg));
        _sftp.Connect();
        RemoteAuth.ApplySocketOptions(_sftp, Cfg);   // TCP_NODELAY / SO_KEEPALIVE
        _cwd = _sftp.WorkingDirectory;
    }

    protected override void WriteWelcome()
    {
        Line($"Connected to {Cfg.Host} over SFTP.");
        Line("Type 'help' for commands, 'exit' to close.");
        Line();
    }

    protected override void ExecuteCommand(string line)
    {
        if (_sftp == null) return;
        var t = Tokenize(line);
        var cmd = t[0].ToLowerInvariant();
        var args = t.GetRange(1, t.Count - 1);

        switch (cmd)
        {
            case "help": case "?": Help(); break;
            case "pwd": Line(_cwd); break;
            case "ls": case "dir": ListDir(args.Count > 0 ? args[0] : "."); break;
            case "cd": ChangeDir(args.Count > 0 ? args[0] : "."); break;
            case "lpwd": Line(LocalDir); break;
            case "lcd": LocalCd(args.Count > 0 ? args[0] : LocalDir); break;
            case "lls": LocalLs(); break;
            case "get": case "download": Get(args); break;
            case "put": case "upload": Put(args); break;
            case "mkdir": Do(() => _sftp.CreateDirectory(args[0]), $"created {ArgOr(args, 0)}"); break;
            case "rmdir": Do(() => _sftp.DeleteDirectory(args[0]), $"removed dir {ArgOr(args, 0)}"); break;
            case "rm": case "del": Do(() => _sftp.DeleteFile(args[0]), $"removed {ArgOr(args, 0)}"); break;
            case "rename": case "mv": Do(() => _sftp.RenameFile(args[0], args[1]), "renamed"); break;
            default: Line($"unknown command: {cmd} (try 'help')"); break;
        }
    }

    private void Help()
    {
        Line("SFTP commands:");
        Line("  ls [path]           list remote directory");
        Line("  cd <path>           change remote directory");
        Line("  pwd                 print remote directory");
        Line("  get <remote> [local]  download a file");
        Line("  put <local> [remote]  upload a file");
        Line("  mkdir/rmdir <path>  create / remove remote directory");
        Line("  rm <path>           delete remote file");
        Line("  rename <old> <new>  rename remote file");
        Line("  lpwd / lcd / lls    local pwd / cd / list");
        Line("  exit                close the session");
    }

    private void ListDir(string path)
    {
        foreach (var f in _sftp!.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            var tag = f.IsDirectory ? "/" : "";
            Line($"{(f.IsDirectory ? "d" : "-")} {f.Length,12}  {f.LastWriteTime:yyyy-MM-dd HH:mm}  {f.Name}{tag}");
        }
    }

    private void ChangeDir(string path)
    {
        _sftp!.ChangeDirectory(path);
        _cwd = _sftp.WorkingDirectory;
    }

    private void LocalCd(string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(LocalDir, path));
        if (!Directory.Exists(full)) { Line("no such local directory: " + full); return; }
        LocalDir = full;
        Line(LocalDir);
    }

    private void LocalLs()
    {
        foreach (var d in Directory.GetDirectories(LocalDir)) Line("d  " + Path.GetFileName(d) + "/");
        foreach (var f in Directory.GetFiles(LocalDir)) Line($"-  {Path.GetFileName(f)}  ({new FileInfo(f).Length} bytes)");
    }

    private void Get(List<string> args)
    {
        if (args.Count == 0) { Line("usage: get <remote> [local]"); return; }
        var remote = args[0];
        var localName = args.Count > 1 ? args[1] : Path.GetFileName(remote.TrimEnd('/'));
        var localPath = Path.IsPathRooted(localName) ? localName : Path.Combine(LocalDir, localName);
        LocalFile.WriteViaTemp(localPath, tmp =>
        {
            using var fs = File.Create(tmp);
            _sftp!.DownloadFile(remote, fs);
        });
        Line($"downloaded {remote} -> {localPath} ({new FileInfo(localPath).Length} bytes)");
    }

    private void Put(List<string> args)
    {
        if (args.Count == 0) { Line("usage: put <local> [remote]"); return; }
        var localPath = Path.IsPathRooted(args[0]) ? args[0] : Path.Combine(LocalDir, args[0]);
        if (!File.Exists(localPath)) { Line("local file not found: " + localPath); return; }
        var remote = args.Count > 1 ? args[1] : Path.GetFileName(localPath);
        using (var fs = File.OpenRead(localPath)) _sftp!.UploadFile(fs, remote);
        Line($"uploaded {localPath} -> {remote} ({new FileInfo(localPath).Length} bytes)");
    }

    private void Do(Action action, string ok)
    {
        action();
        Line(ok);
    }

    private static string ArgOr(List<string> a, int i) => i < a.Count ? a[i] : "";

    protected override void DisposeClient()
    {
        try { _sftp?.Disconnect(); } catch { }
        _sftp?.Dispose();
        _sftp = null;
    }
}
