using System.Net;
using System.Text;
using AgVpsUnlock.Core;
using Xunit;

namespace AgVpsUnlock.Tests;

public class ServerProbeTests
{
    [Fact]
    public void BuildQuery_MatchesWireFormat()
    {
        byte[] expected =
        [
            0x12, 0x34,             // id
            0x01, 0x00,             // recursion desired
            0x00, 0x01, 0, 0, 0, 0, 0, 0,
            0x01, (byte)'a',
            0x02, (byte)'b', (byte)'c',
            0x00,
            0x00, 0x01,             // type A
            0x00, 0x01              // class IN
        ];
        Assert.Equal(expected, ServerProbe.BuildQuery("a.bc", 0x1234));
    }

    [Fact]
    public void IsValidReply_AcceptsProperResponse() =>
        Assert.True(ServerProbe.IsValidReply(Header(0x12, 0x34, 0x81, 0x80), 0x1234));

    [Fact]
    public void IsValidReply_RejectsWrongTxId() =>
        Assert.False(ServerProbe.IsValidReply(Header(0xAB, 0xCD, 0x81, 0x80), 0x1234));

    [Fact]
    public void IsValidReply_RejectsQueryPacket() =>
        Assert.False(ServerProbe.IsValidReply(Header(0x12, 0x34, 0x01, 0x00), 0x1234));

    [Fact]
    public void IsValidReply_RejectsRcodeError() =>
        Assert.False(ServerProbe.IsValidReply(Header(0x12, 0x34, 0x81, 0x83), 0x1234));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseARecords_ParsesAnswerRegardlessOfQuestionCompression(bool compressedQuestion)
    {
        var ip = new IPAddress([142, 250, 100, 7]);
        var parsed = ServerProbe.ParseARecords(DnsResponse(compressedQuestion, ip));
        Assert.Single(parsed);
        Assert.Equal(ip, parsed[0]);
    }

    [Fact]
    public void ParseARecords_TruncationNeverThrows()
    {
        var full = DnsResponse(false, new IPAddress([1, 2, 3, 4]));
        for (int len = 0; len <= full.Length; len++)
            ServerProbe.ParseARecords(full[..len]);
    }

    [Fact]
    public void ParseARecords_IgnoresNonATypeAnswers()
    {
        var ms = new MemoryStream();
        ms.Write(Header(0x12, 0x34, 0x81, 0x80));
        WriteUncompressedName(ms, "a.bc");
        ms.Write([0x00, 0x01, 0x00, 0x01]);          // question type/class
        ms.Write([0xC0, 0x0C]);                      // answer name -> pointer
        ms.Write([0x00, 0x1C, 0x00, 0x01]);          // AAAA IN
        ms.Write([0, 0, 0, 60, 0x00, 0x10]);         // ttl, rdlength 16
        ms.Write(new byte[16]);                      // AAAA rdata
        Assert.Empty(ServerProbe.ParseARecords(ms.ToArray()));
    }

    [Fact]
    public void DetectLeak_FlagsStrangersIpv6AndUnresolved()
    {
        var vps = IPAddress.Parse("1.2.3.4");
        var resolved = new List<HostResolve>
        {
            new("ok.com", [vps]),
            new("leak.com", [IPAddress.Parse("5.6.7.8"), IPAddress.Parse("2001:db8::1")]),
            new("dead.com", [])
        };
        var (leak, detail) = ServerProbe.DetectLeak(resolved, vps);

        Assert.True(leak);
        Assert.NotNull(detail);
        Assert.Contains("leak.com", detail);
        Assert.Contains("(IPv6!)", detail);
        Assert.Contains("dead.com", detail);
    }

    [Fact]
    public void DetectLeak_CleanWhenAllNamesPointToVps()
    {
        var vps = IPAddress.Parse("1.2.3.4");
        var resolved = new List<HostResolve> { new("a.com", [vps]), new("b.com", [vps]) };
        var (leak, detail) = ServerProbe.DetectLeak(resolved, vps);

        Assert.False(leak);
        Assert.Null(detail);
    }

    private static byte[] Header(byte idHi, byte idLo, byte flagsHi, byte flagsLo) =>
        [idHi, idLo, flagsHi, flagsLo, 0x00, 0x01, 0x00, 0x01, 0, 0, 0, 0];

    private static void WriteUncompressedName(Stream s, string name)
    {
        foreach (var label in name.Split('.'))
        {
            s.Write([(byte)label.Length]);
            s.Write(Encoding.ASCII.GetBytes(label));
        }
        s.Write([0x00]);
    }

    private static byte[] DnsResponse(bool compressedQuestion, IPAddress ip)
    {
        var ms = new MemoryStream();
        ms.Write(Header(0x12, 0x34, 0x81, 0x80));   // QR=1, RCODE=0
        if (compressedQuestion)
        {
            ms.Write([0xC0, 0x0C]);                 // question name as pointer
        }
        else
        {
            WriteUncompressedName(ms, "daily-cloudcode-pa.googleapis.com");
        }
        ms.Write([0x00, 0x01, 0x00, 0x01]);         // type A, class IN

        ms.Write([0xC0, 0x0C]);                     // answer name pointer
        ms.Write([0x00, 0x01, 0x00, 0x01]);         // A IN
        ms.Write([0, 0, 0, 60]);                    // ttl
        ms.Write([0x00, 0x04]);                     // rdlength
        ms.Write(ip.GetAddressBytes());             // rdata
        return ms.ToArray();
    }
}
