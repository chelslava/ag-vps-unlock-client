using AgVpsUnlock.Core;
using Xunit;

namespace AgVpsUnlock.Tests;

public class HostsManagerTests
{
    [Fact]
    public void StripBlock_RemovesOnlyOurBlock()
    {
        const string input =
            "keep1\r\n" +
            "# AG_VPS_UNLOCK_BEGIN\r\n" +
            "1.2.3.4 cloudcode-pa.googleapis.com\r\n" +
            "# AG_VPS_UNLOCK_END\r\n" +
            "keep2";
        Assert.Equal("keep1\r\nkeep2\r\n", HostsManager.StripBlock(input));
    }

    [Fact]
    public void StripBlock_KeepsFileWithoutBlock_Intact()
    {
        const string input = "127.0.0.1 localhost\r\n::1 localhost\r\n";
        Assert.Equal(input, HostsManager.StripBlock(input));
    }
}
