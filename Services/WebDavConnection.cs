using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using MultiSSH.Models;
using Renci.SshNet.Common;

namespace MultiSSH.Services;

/// <summary>An interactive WebDAV session (ls/cd/get/put/…) over HTTP(S).</summary>
public class WebDavConnection : InteractivePromptBackend
{
    private static readonly XNamespace Dav = "DAV:";
    private HttpClient? _http;
    private string _baseUrl = "";
    private string _cwd = "/";

    public WebDavConnection(SessionConfig cfg) : base(cfg) { }

    protected override string PromptText => $"dav {_cwd}> ";

    protected override void ConnectClient()
    {
        ParseTarget();

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Cfg.ConnectTimeoutSeconds > 0 ? Cfg.ConnectTimeoutSeconds + 30 : 60),
        };
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Cfg.Username}:{Cfg.Password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);

        using var resp = PropFind(_cwd, "0");
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new SshAuthenticationException("WebDAV authentication failed (401).");
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"WebDAV connect failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    /// <summary>Turn Host/Port into a base URL and initial path.</summary>
    private void ParseTarget()
    {
        var raw = Cfg.Host.Trim();
        var scheme = "https";
        if (raw.StartsWith("http://")) { scheme = "http"; raw = raw.Substring(7); }
        else if (raw.StartsWith("https://")) { scheme = "https"; raw = raw.Substring(8); }
        else if (Cfg.Port is 80 or 8080) scheme = "http";

        int slash = raw.IndexOf('/');
        var authority = slash < 0 ? raw : raw.Substring(0, slash);
        var basePath = slash < 0 ? "/" : raw.Substring(slash);
        if (!authority.Contains(':') && Cfg.Port is not (80 or 443))
            authority += ":" + Cfg.Port;

        _baseUrl = $"{scheme}://{authority}";
        _cwd = basePath.EndsWith("/") ? basePath : basePath + "/";
    }

    protected override void WriteWelcome()
    {
        Line($"Connected to {_baseUrl} over WebDAV.");
        Line("Type 'help' for commands, 'exit' to close.");
        Line();
    }

    protected override void ExecuteCommand(string line)
    {
        if (_http == null) return;
        var t = Tokenize(line);
        var cmd = t[0].ToLowerInvariant();
        var args = t.GetRange(1, t.Count - 1);

        switch (cmd)
        {
            case "help": case "?": Help(); break;
            case "pwd": Line(_cwd); break;
            case "ls": case "dir": ListDir(args.Count > 0 ? args[0] : "."); break;
            case "cd": ChangeDir(args.Count > 0 ? args[0] : "/"); break;
            case "get": case "download": Get(args); break;
            case "put": case "upload": Put(args); break;
            case "mkdir": Mkcol(args); break;
            case "rm": case "del": case "rmdir": Delete(args); break;
            case "lpwd": Line(LocalDir); break;
            case "lcd": LocalCd(args.Count > 0 ? args[0] : LocalDir); break;
            case "lls": LocalLs(); break;
            default: Line($"unknown command: {cmd} (try 'help')"); break;
        }
    }

    private void Help()
    {
        Line("WebDAV commands:");
        Line("  ls [path]           list a collection");
        Line("  cd <path>           change collection");
        Line("  get <remote> [local]  download a file");
        Line("  put <local> [remote]  upload a file");
        Line("  mkdir <path>        create a collection");
        Line("  rm <path>           delete a file/collection");
        Line("  lpwd / lcd / lls    local pwd / cd / list");
        Line("  exit                close the session");
    }

    // ---- HTTP helpers ----

    private static string Encode(string path) =>
        string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    private string Url(string path) => _baseUrl + Encode(Normalize(path));

    private string Resolve(string arg) =>
        Normalize(arg.StartsWith("/") ? arg : (_cwd.EndsWith("/") ? _cwd : _cwd + "/") + arg);

    private static string Normalize(string path)
    {
        var trailing = path.EndsWith("/");
        var parts = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        var result = "/" + string.Join("/", parts);
        return trailing && result.Length > 1 ? result + "/" : result;
    }

    private HttpResponseMessage PropFind(string path, string depth)
    {
        var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUrl + Encode(path));
        req.Headers.Add("Depth", depth);
        req.Content = new StringContent(
            "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:allprop/></d:propfind>",
            Encoding.UTF8, "application/xml");
        return _http!.Send(req);
    }

    private void ListDir(string arg)
    {
        var path = Resolve(arg);
        if (!path.EndsWith("/")) path += "/";
        using var resp = PropFind(path, "1");
        resp.EnsureSuccessStatusCode();
        var doc = XDocument.Parse(resp.Content.ReadAsStringAsync().Result);

        foreach (var r in doc.Descendants(Dav + "response"))
        {
            var href = r.Element(Dav + "href")?.Value ?? "";
            var hrefPath = href.Contains("://") ? new Uri(href).AbsolutePath : href;
            hrefPath = Uri.UnescapeDataString(hrefPath);

            if (Normalize(hrefPath) == Normalize(path)) continue; // the collection itself

            var isColl = r.Descendants(Dav + "collection").Any();
            var len = r.Descendants(Dav + "getcontentlength").FirstOrDefault()?.Value ?? "";
            var name = hrefPath.TrimEnd('/');
            name = name.Substring(name.LastIndexOf('/') + 1);
            Line($"{(isColl ? "d" : "-")} {len,12}  {name}{(isColl ? "/" : "")}");
        }
    }

    private void ChangeDir(string arg)
    {
        var path = Resolve(arg);
        if (!path.EndsWith("/")) path += "/";
        using var resp = PropFind(path, "0");
        if (!resp.IsSuccessStatusCode) { Line($"cannot cd: {(int)resp.StatusCode}"); return; }
        _cwd = path;
    }

    private void Get(List<string> args)
    {
        if (args.Count == 0) { Line("usage: get <remote> [local]"); return; }
        var remote = Resolve(args[0]);
        var localName = args.Count > 1 ? args[1] : Path.GetFileName(remote.TrimEnd('/'));
        var localPath = Path.IsPathRooted(localName) ? localName : Path.Combine(LocalDir, localName);
        using var resp = _http!.Send(new HttpRequestMessage(HttpMethod.Get, Url(remote)));
        resp.EnsureSuccessStatusCode();
        LocalFile.WriteViaTemp(localPath, tmp =>
        {
            using var fs = File.Create(tmp);
            resp.Content.ReadAsStream().CopyTo(fs);
        });
        Line($"downloaded {remote} -> {localPath} ({new FileInfo(localPath).Length} bytes)");
    }

    private void Put(List<string> args)
    {
        if (args.Count == 0) { Line("usage: put <local> [remote]"); return; }
        var localPath = Path.IsPathRooted(args[0]) ? args[0] : Path.Combine(LocalDir, args[0]);
        if (!File.Exists(localPath)) { Line("local file not found: " + localPath); return; }
        var remote = Resolve(args.Count > 1 ? args[1] : Path.GetFileName(localPath));
        using var fs = File.OpenRead(localPath);
        var req = new HttpRequestMessage(HttpMethod.Put, Url(remote)) { Content = new StreamContent(fs) };
        using var resp = _http!.Send(req);
        resp.EnsureSuccessStatusCode();
        Line($"uploaded {localPath} -> {remote} ({new FileInfo(localPath).Length} bytes)");
    }

    private void Mkcol(List<string> args)
    {
        if (args.Count == 0) { Line("usage: mkdir <path>"); return; }
        var path = Resolve(args[0]);
        using var resp = _http!.Send(new HttpRequestMessage(new HttpMethod("MKCOL"), Url(path)));
        resp.EnsureSuccessStatusCode();
        Line("created " + path);
    }

    private void Delete(List<string> args)
    {
        if (args.Count == 0) { Line("usage: rm <path>"); return; }
        var path = Resolve(args[0]);
        using var resp = _http!.Send(new HttpRequestMessage(HttpMethod.Delete, Url(path)));
        resp.EnsureSuccessStatusCode();
        Line("removed " + path);
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
        _http?.Dispose();
        _http = null;
    }
}
