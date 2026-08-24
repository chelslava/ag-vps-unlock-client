using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace AgVpsUnlock.Core;

public sealed record ProbeResult(
    bool TcpOk,
    bool TlsOk,
    string? CertificateSubject,
    bool DnsReachable,
    bool DnsHijacked,
    string? Error);

/// <summary>
/// Checks that the VPS actually fronts the routed endpoints. The TLS handshake
/// is the authoritative test: it proves both reachability and that the SNI
/// forwarder passes through to Google (the returned certificate must be a
/// Google one). The UDP DNS check is informational - many residential ISPs
/// transparently answer port 53 themselves, in which case NRPT-style routing is
/// dead on arrival and hosts pinning is the only path that works.
/// </summary>
public static class ServerProbe
{
    public static async Task<ProbeResult> ProbeAsync(string ip, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ip, out var addr))
            return new ProbeResult(false, false, null, false, false, "Некорректный IP-адрес");

        // --- TLS over TCP 443 with a routed SNI ---
        bool tcpOk = false, tlsOk = false;
        string? subject = null;
        string? error = null;
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(addr, 443, ct).AsTask();
            var done = await Task.WhenAny(connectTask, Task.Delay(4000, ct));
            if (done != connectTask || !client.Connected)
                throw new SocketException(10060); // TIMED_OUT
            tcpOk = true;

            using var ssl = new SslStream(client.GetStream(), false, (_, cert, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "daily-cloudcode-pa.googleapis.com"
            }, ct);
            subject = ssl.RemoteCertificate?.Subject;
            tlsOk = subject?.Contains("google", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
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
            var done = await Task.WhenAny(recvTask, Task.Delay(2500, ct));
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

        return new ProbeResult(tcpOk, tlsOk, subject, dnsReachable, hijacked, tlsOk ? null : error);
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
