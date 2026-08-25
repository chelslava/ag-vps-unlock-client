using System.Text;
using AgVpsUnlock.Core;
using Xunit;

namespace AgVpsUnlock.Tests;

public class BinaryPatcherTests
{
    [Fact]
    public void CountNames_CountsBothNames()
    {
        var data = Encoding.ASCII.GetBytes("aaa ineligible bbb inexigible ccc");
        var (newCount, oldCount) = BinaryPatcher.CountNames(new MemoryStream(data));
        Assert.Equal(1, oldCount);
        Assert.Equal(1, newCount);
    }

    [Fact]
    public void CountNames_FindsPatternSpanningChunkBoundary()
    {
        const int chunk = 1 << 20;
        var data = new byte[chunk + 100];
        Array.Fill(data, (byte)'a');
        // The internal buffer holds exactly chunk + (needleLen - 1) bytes, so a
        // pattern starting inside that overlap window and ending past it forces
        // the two-pass boundary carry to do its job.
        Encoding.ASCII.GetBytes("ineligible").CopyTo(data, chunk + 4);
        var (_, oldCount) = BinaryPatcher.CountNames(new ChunkyStream(new MemoryStream(data), 4096));
        Assert.Equal(1, oldCount);
    }

    [Fact]
    public void Swap_PatchesDetectsAlreadyAndRestores()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("HEAD-ineligible-TAIL"));

            Assert.Equal(BinaryPatcher.Result.Replaced, BinaryPatcher.Swap(path, "ineligible", "inexigible"));
            Assert.Contains("inexigible", File.ReadAllText(path));

            Assert.Equal(BinaryPatcher.Result.Already, BinaryPatcher.Swap(path, "ineligible", "inexigible"));

            Assert.Equal(BinaryPatcher.Result.Replaced, BinaryPatcher.Swap(path, "inexigible", "ineligible"));
            Assert.DoesNotContain("inexigible", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Swap_ReturnsNotFound_WhenNoSignaturePresent()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("nothing to see"));
            Assert.Equal(BinaryPatcher.Result.NotFound, BinaryPatcher.Swap(path, "ineligible", "inexigible"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindInstalls_IncludesCustomDirectBinary()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "custom_language_server.exe");
        try
        {
            File.WriteAllBytes(tempFile, Encoding.ASCII.GetBytes("dummy"));
            var installs = BinaryPatcher.FindInstalls([tempFile]);
            Assert.Contains(installs, i => i.Binaries.Contains(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}

/// <summary>Serves reads in small pieces to force the chunk-boundary logic.</summary>
file sealed class ChunkyStream : Stream
{
    private readonly Stream _inner;
    private readonly int _maxPiece;

    public ChunkyStream(Stream inner, int maxPiece)
    {
        _inner = inner;
        _maxPiece = maxPiece;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, Math.Min(count, _maxPiece));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
