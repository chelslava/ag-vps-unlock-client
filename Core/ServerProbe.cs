using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace AgVpsUnlock.Core;

/// <summary>System-resolver answer for one routed hostname (hosts file included).</summary>
public sealed record HostResolve(string Host, IReadOnlyList<IPAddress> Addresses);

public sealed record ProbeResult(
    bool TcpOk,
    bool TlsOk,
    string? CertificateSubject,
    bool DnsReachable,
    bool DnsHijacked,
    string? Error,
    /// <summary>How each routed name resolves on THIS machine — empty when not requested.</summary>
    IReadOnlyList<HostResolve> Resolved,
    /// <summary>True when at least one routed name resolves to an address other than the VPS:
    /// traffic for it bypasses the tunnel (stale/missing hosts block or an IPv6 AAAA leak),
    /// which is exactly what produces Google's "User location is not supported".</summary>
    bool RoutingLeak,
    /// <summary>Human-readable summary of the leaking names.</summary>
    string? LeakDetail);

/// <summary>
/// Checks that the VPS actually fronts the routed endpoints. The TLS handshake
/// is the authoritative test: it proves both reachability and that the SNI
/// forwarder passes through to Google (the returned certificate must be a
/// Google one). Every phase is time-bounded so the probe can never hang the UI.
/// The UDP DNS check is informational - many residential ISPs transparently
/// answer port 53 themselves, in which case NRPT-style routing is dead on
/// arrival and hosts pinning is the only path that works.
/// </summary>
public static class ServerProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DnsUdpTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(4);

    public static async Task<ProbeResult> ProbeAsync(
        string ip, IEnumerable<string>? routedHosts = null, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ip, out var addr))
        {
            return new ProbeResult(false, false, null, false, false, "Некорректный IP-адрес",
                [], false, null);
        }

        // --- TLS over TCP 443 with a routed SNI (bounded end-to-end) ---
        var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TlsTimeout);

        bool tcpOk = false, tlsOk = false;
        string? subject = null;
        string? error = null;
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(addr, 443, probeCts.Token).AsTask();
            var done = await Task.WhenAny(connectTask, Task.Delay(ConnectTimeout, probeCts.Token));
            if (done != connectTask || !client.Connected)
                throw new SocketException(10060); // TIMED_OUT
            tcpOk = true;

            using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "daily-cloudcode-pa.googleapis.com"
            }, probeCts.Token);
            subject = ssl.RemoteCertificate?.Subject;
            tlsOk = subject?.Contains("google", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = tcpOk ? "таймаут TLS-рукопожатия" : "таймаут подключения";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            probeCts.Dispose();
        }

        // --- Plain UDP A query for a routed name ---
        bool dnsReachable = false, hijacked = false;
        try
        {
            using var udp = new UdpClient();
            udp.Connect(addr, 53);
            var query = BuildQuery("daily-cloudcode-pa.googleapis.com");
            await udp.SendAsync(query, ct);
            var recvTask = udp.ReceiveAsync(ct).AsTask();
            var done = await Task.WhenAny(recvTask, Task.Delay(DnsUdpTimeout, ct));
            if (done == recvTask)
            {
                dnsReachable = true;
                var answer = ParseARecords(recvTask.Result.Buffer);
                // Our dnsmasq answers with the VPS itself. Genuine Google
                // addresses mean somebody else answered on the way.
                hijacked = answer.All(a => !a.Equals(addr)) && answer.Count > 0;
            }
        }
        catch
        {
            // unreachable DNS is reported via flags alone
        }

        // --- Do the routed names actually resolve to the VPS on this machine? ---
        List<HostResolve>? resolved = null;
        bool leak = false;
        string? leakDetail = null;
        try
        {
            resolved = await ResolveAll(routedHosts, ct);
            (leak, leakDetail) = DetectLeak(resolved, addr);
        }
        catch
        {
            // diagnostics only - never fail the probe because of it
        }

        return new ProbeResult(tcpOk, tlsOk, subject, dnsReachable, hijacked,
            tlsOk ? null : error, resolved ?? [], leak, leakDetail);
    }

    private static async Task<List<HostResolve>> ResolveAll(
        IEnumerable<string>? hosts, CancellationToken ct)
    {
        var result = new List<HostResolve>();
        if (hosts is null) return result;

        var tasks = hosts.Select(async h =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ResolveTimeout);
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(h, cts.Token);
                return new HostResolve(h, addrs);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new HostResolve(h, []);
            }
            catch
            {
                return new HostResolve(h, []);
            }
        }).ToList();

        result.AddRange(await Task.WhenAll(tasks));
        return result;
    }

    private static (bool Leak, string? Detail) DetectLeak(
        IReadOnlyList<HostResolve> resolved, IPAddress vps)
    {
        var bad = new List<string>();
        foreach (var r in resolved.Where(r => r.Addresses.Count > 0))
        {
            var strangers = r.Addresses.Where(a => !a.Equals(vps)).ToList();
            if (strangers.Count == 0) continue;

            var shown = string.Join(", ", strangers.Select(a =>
                a.AddressFamily == AddressFamily.InterNetworkV6 ? $"{a} (IPv6!)" : a.ToString()));
            bad.Add($"{r.Host} → {shown}");
        }

        var unresolved = resolved
            .Where(r => r.Addresses.Count == 0)
            .Select(r => $"{r.Host}: имя не резолвится")
            .ToList();

        var parts = bad.Concat(unresolved).ToList();
        return parts.Count == 0
            ? (false, null)
            : (bad.Count > 0, string.Join("; ", parts));
    }

    private static byte[] BuildQuery(string name)
    {
        var q = new List<byte>(name.Length + 18)
        {
            0x12, 0x34,                   // id
            0x01, 0x00,                   // recursion desired
            0x00, 0x01, 0, 0, 0, 0, 0, 0  // one question, nothing else
        };
        foreach (var label in name.Split('.'))
        {
            q.Add((byte)label.Length);
            q.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        q.AddRange([0x00, 0x00, 0x01, 0x00, 0x01]); // A IN
        return q.ToArray();
    }

    private static List<IPAddress> ParseARecords(byte[] buf)
    {
        var ips = new List<IPAddress>();
        if (buf.Length < 12) return ips;
        int questions = (buf[4] << 8) | buf[5];
        int answers = (buf[6] << 8) | buf[7];
        int i = 12;
        for (int q = 0; q < questions && i < buf.Length; q++)
        {
            while (i < buf.Length && buf[i] != 0)
            {
                if ((buf[i] & 0xC0) == 0xC0) { i += 2; goto labels_done; }
                i += buf[i] + 1;
            }
            i += 1 + 4; // root byte + type+class
        labels_done:;
        }
        for (int a = 0; a < answers && i + 10 <= buf.Length; a++)
        {
            // skip name (possibly compressed)
            while (i < buf.Length && buf[i] != 0)
            {
                if ((buf[i] & 0xC0) == 0xC0) { i += 2; goto name_done; }
                i += buf[i] + 1;
            }
            i += 1;
        name_done:;
            int type = (buf[i] << 8) | buf[i + 1];
            int rdlen = (buf[i + 8] << 8) | buf[i + 9];
            i += 10;
            if (type == 1 && rdlen == 4 && i + 4 <= buf.Length)
            {
                ips.Add(new IPAddress(buf.AsSpan(i, 4)));
            }
            i += rdlen;
        }
        return ips;
    }
}
