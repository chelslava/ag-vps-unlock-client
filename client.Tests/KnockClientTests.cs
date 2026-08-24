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
}
