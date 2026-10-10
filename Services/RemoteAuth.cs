using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MultiSSH.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiSSH.Services;

/// <summary>
/// Builds an SSH.NET <see cref="ConnectionInfo"/> from a <see cref="SessionConfig"/>.
/// Shared by the SSH shell and the SFTP/SCP file-transfer backends so they all
/// authenticate identically (password / public key / keyboard-interactive).
/// </summary>
public static class RemoteAuth
{
    public static ConnectionInfo BuildConnectionInfo(SessionConfig cfg)
    {
        var methods = new List<AuthenticationMethod>();

        switch (cfg.Auth)
        {
            case AuthMethod.PublicKey:
                if (string.IsNullOrWhiteSpace(cfg.PrivateKeyPath) || !File.Exists(cfg.PrivateKeyPath))
                    throw new FileNotFoundException("Private key file not found", cfg.PrivateKeyPath);
                PrivateKeyFile keyFile;
                try
                {
                    keyFile = string.IsNullOrEmpty(cfg.KeyPassphrase)
                        ? new PrivateKeyFile(cfg.PrivateKeyPath)
                        : new PrivateKeyFile(cfg.PrivateKeyPath, cfg.KeyPassphrase);
                }
                catch (Exception ex) when (IsPassphraseProblem(ex))
                {
                    throw new KeyPassphraseRequiredException(
                        string.IsNullOrEmpty(cfg.KeyPassphrase)
                            ? "The private key is encrypted and needs a passphrase."
                            : "Incorrect passphrase for the private key.", ex);
                }
                methods.Add(new PrivateKeyAuthenticationMethod(cfg.Username, keyFile));
                break;

            case AuthMethod.KeyboardInteractive:
            {
                var ki = new KeyboardInteractiveAuthenticationMethod(cfg.Username);
                ki.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts) prompt.Response = cfg.Password ?? "";
                };
                methods.Add(ki);
                break;
            }

            case AuthMethod.Agent:
                methods.Add(new PasswordAuthenticationMethod(cfg.Username, cfg.Password ?? ""));
                break;

            default: // Password
                methods.Add(new PasswordAuthenticationMethod(cfg.Username, cfg.Password ?? ""));
                var kip = new KeyboardInteractiveAuthenticationMethod(cfg.Username);
                kip.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts) prompt.Response = cfg.Password ?? "";
                };
                methods.Add(kip);
                break;
        }

        return new ConnectionInfo(ResolveHost(cfg), cfg.Port, cfg.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(cfg.ConnectTimeoutSeconds),
        };
    }

    /// <summary>
    /// Honours the session's Internet-protocol-version preference. "Auto" (and any
    /// explicit IP literal) is returned unchanged; "IPv4"/"IPv6" resolves the host
    /// name to an address of that family so the connection uses the chosen protocol.
    /// May perform a DNS lookup, so call it off the UI thread.
    /// </summary>
    private static string ResolveHost(SessionConfig cfg)
    {
        var pref = cfg.IpVersion;
        if (string.IsNullOrWhiteSpace(pref) || pref.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return cfg.Host;

        // The user typed an explicit address — honour it as-is regardless of the toggle.
        if (IPAddress.TryParse(cfg.Host, out _))
            return cfg.Host;

        var family = pref.Equals("IPv6", StringComparison.OrdinalIgnoreCase)
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;

        var addr = Dns.GetHostAddresses(cfg.Host).FirstOrDefault(a => a.AddressFamily == family);
        if (addr == null)
            throw new InvalidOperationException(
                $"No {pref} address was found for '{cfg.Host}'. " +
                $"Set the Internet protocol version to Auto (or {(family == AddressFamily.InterNetworkV6 ? "IPv4" : "IPv6")}).");
        return addr.ToString();
    }

    // BaseClient.Session is an internal property; the concrete Session holds the
    // live Socket in a private field. Cached so we only resolve the members once.
    private static readonly PropertyInfo? SessionProp =
        typeof(BaseClient).GetProperty("Session", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Applies the low-level TCP options (TCP_NODELAY / SO_KEEPALIVE) to a connected
    /// SSH.NET client's socket. SSH.NET exposes no public hook for these, so we reach
    /// the socket by reflection — best-effort and non-fatal if the internals change.
    /// Call only after the client has connected.
    /// </summary>
    public static void ApplySocketOptions(BaseClient client, SessionConfig cfg)
    {
        try
        {
            var session = SessionProp?.GetValue(client);
            var field = session?.GetType().GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(session) is not Socket socket || !socket.Connected) return;

            socket.NoDelay = cfg.TcpNoDelay;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, cfg.SoKeepalive);
        }
        catch
        {
            // Best-effort: the SSH.NET internal layout differs or the socket is gone.
        }
    }

    /// <summary>
    /// Hard-closes a client's socket. On a link where the server has stopped answering,
    /// SSH.NET's Disconnect()/Dispose() wait on it forever and the requests in flight never
    /// complete; closing the socket underneath fails them at once. Best-effort, like
    /// <see cref="ApplySocketOptions"/>.
    /// </summary>
    public static void AbortSocket(BaseClient client)
    {
        try
        {
            var session = SessionProp?.GetValue(client);
            var field = session?.GetType().GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(session) is Socket socket) socket.Dispose();
        }
        catch
        {
            // Best-effort: the SSH.NET internal layout differs or the socket is gone.
        }
    }

    private static bool IsPassphraseProblem(Exception ex)
    {
        if (ex is SshPassPhraseNullOrEmptyException) return true;
        if (ex is System.Security.Cryptography.CryptographicException) return true;
        var m = ex.Message?.ToLowerInvariant() ?? "";
        return m.Contains("passphrase") || m.Contains("pass phrase")
            || m.Contains("invalid pad") || m.Contains("decrypt")
            || m.Contains("bad data");
    }
}
