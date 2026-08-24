using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AgVpsUnlock.Core;

/// <summary>
/// Single-packet authorization for a locked relay: proves possession of the
/// shared secret so the server joins our IP to its allowlist (setup-vps.sh
/// `lock on`). Packet layout: "AG" | uint64 BE unix-seconds |
/// HMAC-SHA256(token, "agvps|" + ts)[0..16] - 26 bytes total.
/// </summary>
public static class KnockClient
{
    public const int DefaultPort = 1604;

    public static async Task<bool> SendAsync(
        string vpsIp, string token, int port = DefaultPort, CancellationToken ct = default)
    {
        var payload = BuildPayload(token, DateTimeOffset.UtcNow);
        using var udp = new UdpClient();
        udp.Connect(vpsIp, port);
        await udp.SendAsync(payload, ct);
        var recv = udp.ReceiveAsync(ct).AsTask();
        var done = await Task.WhenAny(recv, Task.Delay(2000, ct));
        return done == recv && recv.Result.Buffer.Length > 0;
    }

    internal static byte[] BuildPayload(string token, DateTimeOffset now)
    {
        long ts = now.ToUnixTimeSeconds();
        var payload = new byte[26];
        payload[0] = (byte)'A';
        payload[1] = (byte)'G';
        for (int shift = 56, off = 2; shift >= 0; shift -= 8, off++)
            payload[off] = (byte)(ts >> shift);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
        var mac = hmac.ComputeHash(Encoding.ASCII.GetBytes("agvps|" + ts));
        Array.Copy(mac, 0, payload, 10, 16);
        return payload;
    }
}
