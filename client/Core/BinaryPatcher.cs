using System.Diagnostics;
using System.Text;

namespace AgVpsUnlock.Core;

public sealed record InstallInfo(string Root, string Label, List<string> Binaries);

/// <summary>
/// Finds Antigravity-family installs and swaps the eligibility field name inside
/// their native binaries. The rename is length-preserving ("ineligible" →
/// "inexigible"), so the PE layout, offsets and file size never move - which is
/// what allows patching in place from streamed offsets instead of loading whole
/// ~150 MB images into memory.
/// </summary>
public static class BinaryPatcher
{
    private const string OldName = "ineligible";
    private const string NewName = "inexigible";
    private static readonly byte[] OldBytes = Encoding.ASCII.GetBytes(OldName);
    private static readonly byte[] NewBytes = Encoding.ASCII.GetBytes(NewName);

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

    public static List<InstallInfo> FindInstalls(IEnumerable<string>? customRoots = null)
    {
        var found = new List<InstallInfo>();
        var candidates = CandidateRoots().Concat(customRoots ?? Enumerable.Empty<string>()).Distinct();
        foreach (var cand in candidates)
        {
            if (string.IsNullOrWhiteSpace(cand)) continue;
            var trimmed = cand.Trim();
            if (File.Exists(trimmed))
            {
                var dir = Path.GetDirectoryName(trimmed) ?? trimmed;
                found.Add(new InstallInfo(dir, Path.GetFileName(trimmed), [trimmed]));
                continue;
            }
            if (!LooksLikeInstall(trimmed)) continue;
            var bins = BinaryTargets(trimmed).Where(File.Exists).Distinct().ToList();
            if (bins.Count == 0) continue;
            found.Add(new InstallInfo(trimmed, Path.GetFileName(trimmed), bins));
        }
        return found;
    }

    public enum BinaryState { Patched, Unpatched, Unknown }

    /// <summary>Single chunked pass over the file: enough to tell patched
    /// from stock without loading it into memory.</summary>
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

    /// <summary>Vectorized streaming count of both names over an arbitrary
    /// stream. Chunks carry a needle-length-1 tail overlap so occurrences that
    /// straddle chunk boundaries are still found exactly once.</summary>
    internal static (int newCount, int oldCount) CountNames(Stream stream)
    {
        const int ChunkSize = 1 << 20;
        int overlap = OldBytes.Length - 1;
        var buf = new byte[ChunkSize + overlap];
        int tail = 0;
        int newCount = 0, oldCount = 0;
        while (true)
        {
            int total = tail;
            int read;
            while (total < buf.Length && (read = stream.Read(buf, total, buf.Length - total)) > 0)
                total += read;
            if (total == 0) break;

            var span = buf.AsSpan(0, total);
            int i = 0;
            while (i < total)
            {
                var rest = span.Slice(i);
                int rn = rest.IndexOf(NewBytes);
                int ro = rest.IndexOf(OldBytes);
                if (rn < 0 && ro < 0) break;
                if (ro < 0 || (rn >= 0 && rn <= ro))
                {
                    newCount++;
                    i += rn + NewBytes.Length;
                }
                else
                {
                    oldCount++;
                    i += ro + OldBytes.Length;
                }
            }

            if (total < buf.Length) break; // EOF
            tail = overlap;
            Buffer.BlockCopy(buf, total - tail, buf, 0, tail);
        }
        return (newCount, oldCount);
    }

    public delegate void LogFn(string message);

    /// <summary>
    /// Rewrites every binary of every install. Running executables lock their
    /// image on Windows, so a failed write retries once after killing the owner.
    /// </summary>
    public static (int patched, int alreadyPatched, int failed) ApplyAll(LogFn log, IEnumerable<string>? customRoots = null)
    {
        int patched = 0, already = 0, failed = 0;
        foreach (var inst in FindInstalls(customRoots))
        {
            foreach (var bin in inst.Binaries)
            {
                try
                {
                    switch (Swap(bin, OldName, NewName))
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

    public static (int restored, int failed) RestoreAll(LogFn log, IEnumerable<string>? customRoots = null)
    {
        int restored = 0, failed = 0;
        foreach (var inst in FindInstalls(customRoots))
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

    internal enum Result { Replaced, Already, NotFound }

    internal static Result Swap(string path, string from, string to)
    {
        if (from.Length != to.Length) throw new InvalidOperationException("length must match");
        var offsets = ScanOffsets(path, from, out bool hasOther);
        if (offsets.Count > 0)
        {
            WriteAt(path, offsets, to);
            return Result.Replaced;
        }
        return hasOther ? Result.Already : Result.NotFound;
    }

    /// <summary>One streamed pass collecting absolute offsets of every
    /// <paramref name="from"/> occurrence; <paramref name="hasOther"/> reports
    /// whether the opposite name is present anywhere (Already detection).</summary>
    private static List<long> ScanOffsets(string path, string from, out bool hasOther)
    {
        var fromBytes = Encoding.ASCII.GetBytes(from);
        var otherBytes = from == OldName ? NewBytes : OldBytes;

        hasOther = false;
        var offsets = new List<long>();

        using var fs = File.OpenRead(path);
        const int ChunkSize = 1 << 20;
        int overlap = fromBytes.Length - 1;
        var buf = new byte[ChunkSize + overlap];
        int tail = 0;
        long baseOffset = 0;
        while (true)
        {
            int total = tail;
            int read;
            while (total < buf.Length && (read = fs.Read(buf, total, buf.Length - total)) > 0)
                total += read;
            if (total == 0) break;

            var span = buf.AsSpan(0, total);
            int i = 0;
            while (i < total)
            {
                var rest = span.Slice(i);
                int rf = rest.IndexOf(fromBytes);
                int ro = rest.IndexOf(otherBytes);
                if (rf < 0 && ro < 0) break;
                if (ro < 0 || (rf >= 0 && rf <= ro))
                {
                    offsets.Add(baseOffset + i + rf);
                    i += rf + fromBytes.Length;
                }
                else
                {
                    hasOther = true;
                    i += ro + otherBytes.Length;
                }
            }

            if (total < buf.Length) break; // EOF
            baseOffset += total - tail;
            tail = overlap;
            Buffer.BlockCopy(buf, total - tail, buf, 0, tail);
        }
        return offsets;
    }

    private static void WriteAt(string path, List<long> offsets, string to)
    {
        try
        {
            WriteCore(path, offsets, to);
        }
        catch (IOException)
        {
            // fall through to kill-and-retry
        }
        var name = Path.GetFileName(path);
        using (Process.Start(new ProcessStartInfo("taskkill", $"/F /IM \"{name}\"")
               { CreateNoWindow = true, UseShellExecute = false })) { }
        Thread.Sleep(800);
        WriteCore(path, offsets, to);
    }

    private static void WriteCore(string path, List<long> offsets, string to)
    {
        var bytes = Encoding.ASCII.GetBytes(to);
        using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        foreach (var off in offsets)
        {
            fs.Seek(off, SeekOrigin.Begin);
            fs.Write(bytes);
        }
    }
}
