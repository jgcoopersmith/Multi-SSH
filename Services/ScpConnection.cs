using System.IO;
using MultiSSH.Models;
using Renci.SshNet;

namespace MultiSSH.Services;

/// <summary>An interactive SCP session (get/put file copies) over SSH via SSH.NET.</summary>
public class ScpConnection : InteractivePromptBackend
{
    private ScpClient? _scp;

    public ScpConnection(SessionConfig cfg) : base(cfg) { }

    protected override string PromptText => "scp> ";

    protected override void ConnectClient()
    {
        _scp = new ScpClient(RemoteAuth.BuildConnectionInfo(Cfg));
        _scp.Connect();
        RemoteAuth.ApplySocketOptions(_scp, Cfg);   // TCP_NODELAY / SO_KEEPALIVE
    }

    protected override void WriteWelcome()
    {
        Line($"Connected to {Cfg.Host} over SCP.");
        Line("SCP copies files only. Use: get <remote> [local], put <local> [remote].");
        Line("Type 'help' for commands, 'exit' to close.");
        Line();
    }

    protected override void ExecuteCommand(string line)
    {
        if (_scp == null) return;
        var t = Tokenize(line);
        var cmd = t[0].ToLowerInvariant();
        var args = t.GetRange(1, t.Count - 1);

        switch (cmd)
        {
            case "help": case "?": Help(); break;
            case "get": case "download": Get(args); break;
            case "put": case "upload": Put(args); break;
            case "lpwd": Line(LocalDir); break;
            case "lcd": LocalCd(args.Count > 0 ? args[0] : LocalDir); break;
            case "lls": LocalLs(); break;
            default: Line($"unknown command: {cmd} (try 'help')"); break;
        }
    }

    private void Help()
    {
        Line("SCP commands:");
        Line("  get <remote> [local]  download a remote file");
        Line("  put <local> [remote]  upload a local file");
        Line("  lpwd / lcd / lls      local pwd / cd / list");
        Line("  exit                  close the session");
    }

    private void Get(List<string> args)
    {
        if (args.Count == 0) { Line("usage: get <remote> [local]"); return; }
        var remote = args[0];
        var localName = args.Count > 1 ? args[1] : Path.GetFileName(remote.TrimEnd('/'));
        var localPath = Path.IsPathRooted(localName) ? localName : Path.Combine(LocalDir, localName);
        LocalFile.WriteViaTemp(localPath, tmp => _scp!.Download(remote, new FileInfo(tmp)));
        Line($"downloaded {remote} -> {localPath} ({new FileInfo(localPath).Length} bytes)");
    }

    private void Put(List<string> args)
    {
        if (args.Count == 0) { Line("usage: put <local> [remote]"); return; }
        var localPath = Path.IsPathRooted(args[0]) ? args[0] : Path.Combine(LocalDir, args[0]);
        if (!File.Exists(localPath)) { Line("local file not found: " + localPath); return; }
        var remote = args.Count > 1 ? args[1] : Path.GetFileName(localPath);
        _scp!.Upload(new FileInfo(localPath), remote);
        Line($"uploaded {localPath} -> {remote} ({new FileInfo(localPath).Length} bytes)");
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

    protected override void DisposeClient()
    {
        try { _scp?.Disconnect(); } catch { }
        _scp?.Dispose();
        _scp = null;
    }
}
