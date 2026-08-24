using System.Diagnostics;
using System.Net;
using System.Text;

namespace AgVpsUnlock.Core;

/// <summary>
/// Manages this app's marked block inside the system hosts file. Everything
/// outside the markers belongs to the system and other tools and is preserved
/// byte for byte.
/// </summary>
/// <remarks>
/// Pinning is the routing mechanism itself, not a fallback: the four endpoints
/// resolve straight to the VPS, so their TLS connections terminate at its SNI
/// forwarder regardless of what the ISP does to outbound port 53.
/// </remarks>
public static class HostsManager
{
    public const string BeginMark = "# AG_VPS_UNLOCK_BEGIN";
    public const string EndMark = "# AG_VPS_UNLOCK_END";

    private static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                     "drivers", "etc", "hosts");

    public static bool IsApplied()
    {
        try { return File.ReadAllText(HostsPath).Contains(BeginMark); }
        catch { return false; }
    }

    public static List<(string Host, IPAddress Ip)> CurrentEntries()
    {
        var list = new List<(string, IPAddress)>();
        try
        {
            bool inside = false;
            foreach (var raw in File.ReadAllLines(HostsPath))
            {
                var line = raw.Trim();
                if (line.StartsWith(BeginMark)) { inside = true; continue; }
                if (inside && line.StartsWith(EndMark)) break;
                if (!inside || line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && IPAddress.TryParse(parts[0], out var ip))
                    list.Add((parts[1], ip));
            }
        }
        catch
        {
            // unreadable hosts = report as empty; Apply will surface the error
        }
        return list;
    }

    /// <summary>Writes (or replaces) our block mapping every host to
    /// <paramref name="ip"/>. Returns false when writing failed.</summary>
    public static bool Apply(IEnumerable<string> hosts, IPAddress ip)
    {
        var sb = new StringBuilder();
        sb.Append("\r\n").AppendLine(BeginMark);
        foreach (var h in hosts)
            sb.Append(ip).Append(' ').AppendLine(h);
        sb.AppendLine(EndMark);

        return Rewrite(existing =>
        {
            var updated = StripBlock(existing);
            if (updated.Length > 0 && !updated.EndsWith('\n'))
                updated += "\r\n";
            return updated + sb.ToString();
        });
    }

    public static bool Remove() => Rewrite(existing => StripBlock(existing));

    internal static string StripBlock(string existing)
    {
        var segments = existing.Split(["\r\n", "\n"], StringSplitOptions.None);
        var kept = new StringBuilder();
        bool inside = false;
        for (int i = 0; i < segments.Length; i++)
        {
            // A trailing newline yields an empty final segment from Split; it is
            // not a real line. Dropping it keeps no-op rewrites byte-stable.
            if (i == segments.Length - 1 && segments[i].Length == 0)
                break;
            var t = segments[i].TrimStart();
            if (t.StartsWith(BeginMark)) { inside = true; continue; }
            if (inside)
            {
                if (t.StartsWith(EndMark)) inside = false;
                continue;
            }
            kept.Append(segments[i]).Append('\n');
        }
        return kept.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private static bool Rewrite(Func<string, string> transform)
    {
        try
        {
            var path = HostsPath;
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var updated = transform(existing);
            if (updated != existing)
                File.WriteAllText(path, updated);
            FlushDnsCache();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void FlushDnsCache()
    {
        using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
        { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(5000);
    }
}
