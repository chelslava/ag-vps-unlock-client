using System.Diagnostics;
using System.Text;

namespace AgVpsUnlock.Core;

public sealed record InstallInfo(string Root, string Label, List<string> Binaries);

/// <summary>
/// Finds Antigravity-family installs and swaps the eligibility field name inside
/// their native binaries. The rename is length-preserving ("ineligible" →
/// "inexigible"), so the PE layout, offsets and file size never move.
/// </summary>
public static class BinaryPatcher
{
    private const string OldName = "ineligible";
    private const string NewName = "inexigible";

    /// <summary>Fixed candidate locations; no registry scan on purpose.</summary>
    public static IReadOnlyList<string> CandidateRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return new[]
        {
            Path.Combine(local, "Programs", "Antigravity"),
            Path.Combine(local, "Programs", "Antigravity IDE"),
            Path.Combine(pf, "Antigravity"),
            Path.Combine(pf, "Antigravity IDE"),
            Path.Combine(pf86, "Antigravity"),
            Path.Combine(pf86, "Antigravity IDE"),
            Path.Combine(local, "agy"),
            Path.Combine(local, "agy", "bin")
        };
    }

    private static bool LooksLikeInstall(string dir) =>
        Directory.Exists(Path.Combine(dir, "resources")) ||
        File.Exists(Path.Combine(dir, "agy.exe")) ||
        File.Exists(Path.Combine(dir, "Antigravity.exe"));

    private static IEnumerable<string> BinaryTargets(string root)
    {
        yield return Path.Combine(root, "agy.exe");
        yield return Path.Combine(root, "resources", "bin", "language_server.exe");
        var extBin = Path.Combine(root, "resources", "app", "extensions", "antigravity", "bin");
        yield return Path.Combine(extBin, "language_server_windows_x64.exe");
        yield return Path.Combine(extBin, "language_server.exe");
    }

    public static List<InstallInfo> FindInstalls()
    {
        var found = new List<InstallInfo>();
        foreach (var cand in CandidateRoots())
        {
            if (!LooksLikeInstall(cand)) continue;
            var bins = BinaryTargets(cand).Where(File.Exists).ToList();
            if (bins.Count == 0) continue;
            found.Add(new InstallInfo(cand, Path.GetFileName(cand), bins));
        }
        return found;
    }

    public enum BinaryState { Patched, Unpatched, Unknown }

    /// <summary>Single streaming pass over the file: enough to tell patched
    /// from stock without loading ~150 MB into memory.</summary>
    public static BinaryState Inspect(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var (newCount, oldCount) = CountNames(fs);
            if (newCount > 0) return BinaryState.Patched;
            if (oldCount > 0) return BinaryState.Unpatched;
            return BinaryState.Unknown;
        }
        catch
        {
            return BinaryState.Unknown;
        }
    }

    private static (int newCount, int oldCount) CountNames(FileStream fs)
    {
        var newBuf = Encoding.ASCII.GetBytes(NewName);
        var oldBuf = Encoding.ASCII.GetBytes(OldName);
        int windowLen = newBuf.Length;
        var window = new byte[windowLen];
        int filled = 0;
        int newCount = 0, oldCount = 0;
        int b;
        while ((b = fs.ReadByte()) >= 0)
        {
            window[filled % windowLen] = (byte)b;
            filled++;
            if (filled < windowLen) continue;
            int lastIdx = (filled - 1) % windowLen;
            if (EndsWith(window, lastIdx, newBuf)) newCount++;
            else if (EndsWith(window, lastIdx, oldBuf)) oldCount++;
        }
        return (newCount, oldCount);
    }

    /// <summary>True when the ring buffer <paramref name="window"/> ends with
    /// <paramref name="needle"/>; <paramref name="lastIdx"/> is the ring index
    /// of the most recent byte.</summary>
    private static bool EndsWith(byte[] window, int lastIdx, byte[] needle)
    {
        int len = window.Length;
        for (int i = 0; i < len; i++)
        {
            int idx = (lastIdx + 1 + i - len) % len;
            if (idx < 0) idx += len;
            if (window[idx] != needle[i]) return false;
        }
        return true;
    }

    public delegate void LogFn(string message);

    /// <summary>
    /// Rewrites every binary of every install. Running executables lock their
    /// image on Windows, so a failed write retries once after killing the owner.
    /// </summary>
    public static (int patched, int alreadyPatched, int failed) ApplyAll(LogFn log)
    {
        int patched = 0, already = 0, failed = 0;
        foreach (var inst in FindInstalls())
        {
            foreach (var bin in inst.Binaries)
            {
                try
                {
                    switch (PatchOne(bin))
                    {
                        case Result.Replaced:
                            patched++;
                            log($"[OK] {Path.GetFileName(bin)}: поле переименовано");
                            break;
                        case Result.Already:
                            already++;
                            log($"[OK] {Path.GetFileName(bin)}: уже пропатчен");
                            break;
                        default:
                            failed++;
                            log($"[!!] {Path.GetFileName(bin)}: сигнатура не найдена (новая версия?)");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    log($"[!!] {Path.GetFileName(bin)}: {ex.Message}");
                }
            }
        }
        return (patched, already, failed);
    }

    public static (int restored, int failed) RestoreAll(LogFn log)
    {
        int restored = 0, failed = 0;
        foreach (var inst in FindInstalls())
        {
            foreach (var bin in inst.Binaries)
            {
                try
                {
                    switch (Swap(bin, NewName, OldName))
                    {
                        case Result.Replaced:
                            restored++;
                            log($"[OK] {Path.GetFileName(bin)}: возвращено исходное имя поля");
                            break;
                        case Result.Already:
                            log($"[--] {Path.GetFileName(bin)}: патча нет");
                            break;
                        default:
                            failed++;
                            log($"[!!] {Path.GetFileName(bin)}: сигнатура не найдена");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    log($"[!!] {Path.GetFileName(bin)}: {ex.Message}");
                }
            }
        }
        return (restored, failed);
    }

    private enum Result { Replaced, Already, NotFound }

    private static Result PatchOne(string path) => Swap(path, OldName, NewName);

    private static Result Swap(string path, string from, string to)
    {
        if (from.Length != to.Length) throw new InvalidOperationException("length must match");
        var data = File.ReadAllBytes(path);
        int replaced = ReplaceAll(data, from, to);
        if (replaced == 0)
            return CountOf(data, to) > 0 ? Result.Already : Result.NotFound;
        WriteWithRetry(path, data);
        return Result.Replaced;
    }

    private static int ReplaceAll(byte[] data, string from, string to)
    {
        var f = Encoding.ASCII.GetBytes(from);
        var t = Encoding.ASCII.GetBytes(to);
        int count = 0, i = 0;
        while (i + f.Length <= data.Length)
        {
            bool hit = true;
            for (int j = 0; j < f.Length; j++)
                if (data[i + j] != f[j]) { hit = false; break; }
            if (hit)
            {
                Array.Copy(t, 0, data, i, t.Length);
                count++;
                i += f.Length;
            }
            else i++;
        }
        return count;
    }

    private static int CountOf(byte[] data, string s)
    {
        var n = Encoding.ASCII.GetBytes(s);
        int count = 0;
        for (int i = 0; i + n.Length <= data.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < n.Length; j++)
                if (data[i + j] != n[j]) { hit = false; break; }
            if (hit) count++;
        }
        return count;
    }

    private static void WriteWithRetry(string path, byte[] data)
    {
        try
        {
            File.WriteAllBytes(path, data);
            return;
        }
        catch (IOException)
        {
            // fall through to kill-and-retry
        }
        var name = Path.GetFileName(path);
        using (Process.Start(new ProcessStartInfo("taskkill", $"/F /IM \"{name}\"")
               { CreateNoWindow = true, UseShellExecute = false })) { }
        Thread.Sleep(800);
        File.WriteAllBytes(path, data);
    }
}
