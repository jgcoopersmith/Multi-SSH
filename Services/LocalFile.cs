using System.IO;

namespace MultiSSH.Services;

public static class LocalFile
{
    /// <summary>Write <paramref name="path"/> via a temp file that replaces it only on success,
    /// so a failed download never truncates an existing local file.</summary>
    public static void WriteViaTemp(string path, Action<string> writeTemp)
    {
        var tmp = path + ".part";
        try
        {
            writeTemp(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}
