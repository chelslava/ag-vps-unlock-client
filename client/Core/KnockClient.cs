using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AgVpsUnlock.Core;

/// <summary>Outcome of a knock exchange with the server.</summary>
public enum KnockResult
{
    /// <summary>Server replied 'K' (or any legacy non-'X' ack): token accepted.</summary>
    Accepted,
    /// <summary>Server explicitly replied 'X': token invalid or revoked.</summary>
    Rejected,
    /// <summary>No reply within the timeout (server down, IP/firewall blocked,
    /// or an old server that never answers knocks).</summary>
    NoReply
}

/// <summary>
/// Single-packet authorization for a locked relay: proves possession of the
/// shared secret so the server joins our IP to its allowlist (setup-vps.sh
/// `lock on`). Packet layout: "AG" | uint64 BE unix-seconds |
/// HMAC-SHA256(token, "agvps|" + ts)[0..16] - 26 bytes total.
/// Reply protocol: 'K' = accepted, 'X' = rejected, no reply = unreachable or
/// legacy server (treated as accepted for backward compatibility).
/// </summary>
public static class KnockClient
{
    public const int DefaultPort = 1604;

    public static async Task<KnockResult> SendAsync(
        string vpsIp, string token, int port = DefaultPort, CancellationToken ct = default)
    {
        try
        {
            var payload = BuildPayload(token, DateTimeOffset.UtcNow);
            using var udp = new UdpClient();
            udp.Connect(vpsIp, port);
            await udp.SendAsync(payload, ct);
            using var delayCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, delayCts.Token);
            var result = await udp.ReceiveAsync(linkedCts.Token);
            return Classify(result.Buffer);
        }
        catch
        {
            return KnockResult.NoReply;
        }
    }

    /// <summary>Pure reply-to-result mapping: null/empty => NoReply,
    /// first byte 'X' (0x58) => Rejected, anything else => Accepted.</summary>
    internal static KnockResult Classify(byte[]? reply)
    {
        if (reply is null || reply.Length == 0)
            return KnockResult.NoReply;
        return reply[0] == 0x58 ? KnockResult.Rejected : KnockResult.Accepted;
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
