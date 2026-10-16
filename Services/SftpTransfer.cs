using System.IO;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MultiSSH.Services;

/// <summary>
/// Restartable SFTP file copies for the WSCP file manager.
/// <para>Downloads are written to "&lt;file&gt;.part", which replaces the target only once it is
/// complete. A download cut off by a dropped link keeps its .part, and the next download of
/// that file carries on from the end of it instead of starting over — unless the remote file
/// has been modified since the .part was last written.</para>
/// <para>Uploads write the remote file in place and can carry on from where an interrupted
/// upload of the same file stopped.</para>
/// </summary>
public static class SftpTransfer
{
    public const string PartSuffix = ".part";

    private const int CopyBuffer = 80 * 1024;

    /// <summary>Writes kept in flight while resuming an upload. An SftpFileStream write waits
    /// for the server's reply, so consecutive chunks rotate across this many handles to keep
    /// the link busy, rather than paying one round trip per chunk.</summary>
    private const int UploadLanes = 16;

    /// <summary>Fits one SFTP write request on OpenSSH's 32 KB channel packets.</summary>
    private const int UploadChunk = 30 * 1024;

    /// <summary>Writes in flight when an upload is cut off may not all have landed, and a
    /// server is free to complete them out of order, so the end of the remote file can have
    /// holes. A resumed upload rewrites this much again: SSH.NET's UploadFile keeps up to the
    /// server's channel window in flight (2 MB on OpenSSH), the resume lanes far less.</summary>
    public const long UploadResumeBackoff = 2 * 1024 * 1024;

    /// <summary>Download <paramref name="remotePath"/> to <paramref name="localPath"/> via its
    /// .part file. With <paramref name="resume"/>, an existing .part no longer than the remote
    /// file, and written since the remote file last changed, is continued from its end;
    /// otherwise the download starts over.</summary>
    public static void Download(SftpClient sftp, string remotePath, string localPath, bool resume,
        Action<long, long> progress, CancellationToken ct)
    {
        var part = localPath + PartSuffix;
        // A .part left from before the remote file was last modified holds the old version:
        // appending the rest of the new one would silently produce a corrupt file.
        if (resume && File.Exists(part)
            && File.GetLastWriteTimeUtc(part) < sftp.GetAttributes(remotePath).LastWriteTimeUtc)
            resume = false;
        using (var input = sftp.Open(remotePath, FileMode.Open, FileAccess.Read))
        using (var output = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
        {
            long size = input.Length;
            long offset = resume && output.Length <= size ? output.Length : 0;
            output.SetLength(offset);
            output.Position = offset;
            if (offset > 0) input.Seek(offset, SeekOrigin.Begin);

            var buf = new byte[CopyBuffer];
            long done = offset;
            progress(done, size);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = input.Read(buf, 0, buf.Length);
                if (n == 0) break;
                output.Write(buf, 0, n);
                done += n;
                progress(done, size);
            }
        }
        File.Move(part, localPath, overwrite: true);
    }

    /// <summary>Upload <paramref name="localPath"/> over <paramref name="remotePath"/>. With
    /// <paramref name="resume"/> (an upload of this same file was cut off), carry on from the
    /// remote file's current length; otherwise truncate it and start from the beginning.
    /// <paramref name="started"/> fires (once, possibly on another thread) when the server has
    /// accepted data for this upload, i.e. the remote file is now this upload's own and a
    /// later resume into it is safe.</summary>
    public static void Upload(SftpClient sftp, string localPath, string remotePath, bool resume,
        Action started, Action<long, long> progress, CancellationToken ct)
    {
        using var input = File.OpenRead(localPath);
        long size = input.Length;
        long offset = 0;
        if (resume)
        {
            long have = RemoteSize(sftp, remotePath);
            if (have <= size) offset = Math.Max(0, have - UploadResumeBackoff);
        }

        if (offset == 0)
        {
            // A fresh upload is SSH.NET's own pipelined UploadFile, exactly as before.
            int fired = 0;
            progress(0, size);
            sftp.UploadFile(input, remotePath, canOverride: true, sent =>
            {
                if (Interlocked.Exchange(ref fired, 1) == 0) started();
                progress((long)sent, size);
            });
            return;
        }
        started();
        ResumeUpload(sftp, input, remotePath, offset, size, progress, ct);
    }

    /// <summary>Write the rest of <paramref name="input"/> from <paramref name="offset"/> into
    /// the existing remote file, keeping <see cref="UploadLanes"/> writes in flight.</summary>
    private static void ResumeUpload(SftpClient sftp, FileStream input, string remotePath, long offset,
        long size, Action<long, long> progress, CancellationToken ct)
    {
        var lanes = new SftpFileStream?[UploadLanes];
        var inFlight = new Task?[UploadLanes];
        try
        {
            for (int i = 0; i < lanes.Length; i++)
                lanes[i] = sftp.Open(remotePath, FileMode.Open, FileAccess.Write);

            input.Position = offset;
            long pos = offset;
            progress(pos, size);
            for (int lane = 0; ; lane = (lane + 1) % UploadLanes)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = new byte[UploadChunk];
                int n = input.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false);
                if (n == 0) break;
                // This lane's previous write went out UploadLanes chunks ago. Waiting for it caps
                // the writes in flight, which is what bounds the holes UploadResumeBackoff covers.
                inFlight[lane]?.GetAwaiter().GetResult();
                inFlight[lane] = WriteAtAsync(lanes[lane]!, pos, chunk, n, ct);
                pos += n;
                progress(pos, size);
            }
            foreach (var t in inFlight) t?.GetAwaiter().GetResult();
        }
        finally
        {
            // Let writes still in flight settle before their handles close underneath them.
            var pending = inFlight.Where(t => t != null).Cast<Task>().ToArray();
            try { Task.WaitAll(pending, TimeSpan.FromSeconds(5)); } catch { /* reported above */ }
            foreach (var s in lanes) { try { s?.Dispose(); } catch { /* link gone */ } }
        }
    }

    private static async Task WriteAtAsync(SftpFileStream s, long at, byte[] data, int count, CancellationToken ct)
    {
        s.Seek(at, SeekOrigin.Begin);
        await s.WriteAsync(data.AsMemory(0, count), ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>True when a transfer failed because the SSH link went away (or Reconnect /
    /// closing the pane cut it on purpose), rather than because of the file operation itself.</summary>
    public static bool IsLinkLoss(Exception ex, SftpClient sftp)
    {
        if (ex is OperationCanceledException or SshConnectionException or SocketException or ObjectDisposedException)
            return true;
        try { return !sftp.IsConnected; }
        catch { return true; }   // a disposed client throws here
    }

    private static long RemoteSize(SftpClient sftp, string path)
    {
        try { return sftp.GetAttributes(path).Size; }
        catch (SftpPathNotFoundException) { return 0; }
    }
}
