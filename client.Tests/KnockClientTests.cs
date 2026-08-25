using System.Security.Cryptography;
using System.Text;
using AgVpsUnlock.Core;
using Xunit;

namespace AgVpsUnlock.Tests;

public class KnockClientTests
{
    private static readonly DateTimeOffset Ts = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Payload_Is26BytesWithMagic()
    {
        var p = KnockClient.BuildPayload("secret", Ts);
        Assert.Equal(26, p.Length);
        Assert.Equal((byte)'A', p[0]);
        Assert.Equal((byte)'G', p[1]);
    }

    [Fact]
    public void Payload_TimestampIsBigEndianAtOffset2()
    {
        var p = KnockClient.BuildPayload("secret", Ts);
        long ts = Ts.ToUnixTimeSeconds();
        for (int shift = 56, off = 2; shift >= 0; shift -= 8, off++)
            Assert.Equal((byte)(ts >> shift), p[off]);
    }

    [Fact]
    public void Payload_DeterministicAndSensitiveToTokenAndTime()
    {
        var a1 = KnockClient.BuildPayload("tok", Ts);
        var a2 = KnockClient.BuildPayload("tok", Ts);
        var otherToken = KnockClient.BuildPayload("tok2", Ts);
        var otherTime = KnockClient.BuildPayload("tok", Ts.AddSeconds(1));

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, otherToken);
        Assert.NotEqual(a1, otherTime);
        // HMAC occupies bytes 10..25; nothing before offset 10 depends on it.
        Assert.True(a1.AsSpan(0, 10).SequenceEqual(otherToken.AsSpan(0, 10)));
    }

    [Fact]
    public void Payload_MatchesGoldenHmacVector()
    {
        const string token = "golden-token-2026";
        var payload = KnockClient.BuildPayload(token, Ts);

        long ts = Ts.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
        byte[] mac = hmac.ComputeHash(Encoding.UTF8.GetBytes("agvps|" + ts));

        Assert.Equal(26, payload.Length);
        Assert.Equal((byte)'A', payload[0]);
        Assert.Equal((byte)'G', payload[1]);
        for (int i = 0; i < 8; i++)
            Assert.Equal((byte)(ts >> (56 - 8 * i)), payload[2 + i]);
        for (int i = 0; i < 16; i++)
            Assert.Equal(mac[i], payload[10 + i]);
    }

    [Theory]
    [InlineData(new byte[] { 0x58 }, KnockResult.Rejected)]           // 'X'
    [InlineData(new byte[] { 0x58, 0x01, 0x02 }, KnockResult.Rejected)]
    [InlineData(new byte[] { 0x4B }, KnockResult.Accepted)]           // 'K'
    [InlineData(new byte[] { 0x4B, 0xFF }, KnockResult.Accepted)]
    [InlineData(new byte[] { 0xAA }, KnockResult.Accepted)]           // legacy random ack
    [InlineData(new byte[] { 0x00 }, KnockResult.Accepted)]
    public void Classify_MapsFirstByte(byte[] reply, KnockResult expected)
        => Assert.Equal(expected, KnockClient.Classify(reply));

    [Fact]
    public void Classify_NullReply_IsNoReply()
        => Assert.Equal(KnockResult.NoReply, KnockClient.Classify(null));

    [Fact]
    public void Classify_EmptyReply_IsNoReply()
        => Assert.Equal(KnockResult.NoReply, KnockClient.Classify(Array.Empty<byte>()));
}
