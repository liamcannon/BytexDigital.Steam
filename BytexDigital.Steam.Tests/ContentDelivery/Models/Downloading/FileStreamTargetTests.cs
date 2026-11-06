using BytexDigital.Steam.ContentDelivery.Models.Downloading;

namespace BytexDigital.Steam.Tests.ContentDelivery.Models.Downloading;

public class FileStreamTargetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bytex_target_{Guid.NewGuid():N}");

    public FileStreamTargetTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task NothingIsCreatedUntilTheFirstWrite()
    {
        await using var target = new FileStreamTarget(PathFor("a.bin"), 10);

        Assert.False(File.Exists(PathFor("a.bin")));
        Assert.Null(target.FileStream);
    }

    [Fact]
    public async Task FirstWriteCreatesTheFileAtItsFinalSize()
    {
        await using var target = new FileStreamTarget(PathFor("a.bin"), 10);

        await target.WriteAsync(0, new byte[] { 1, 2, 3 });
        await target.CompleteAsync();

        Assert.Equal(10, new FileInfo(PathFor("a.bin")).Length);
    }

    [Fact]
    public async Task ChunksCanBeWrittenOutOfOrder()
    {
        await using var target = new FileStreamTarget(PathFor("a.bin"), 10);

        await target.WriteAsync(5, "world"u8.ToArray());
        await target.WriteAsync(0, "hello"u8.ToArray());
        await target.CompleteAsync();

        Assert.Equal("helloworld"u8.ToArray(), File.ReadAllBytes(PathFor("a.bin")));
    }

    [Fact]
    public async Task AnExistingFileIsReplacedNotAppendedTo()
    {
        File.WriteAllBytes(PathFor("a.bin"), Enumerable.Repeat((byte) 9, 50).ToArray());
        await using var target = new FileStreamTarget(PathFor("a.bin"), 4);

        await target.WriteAsync(0, new byte[] { 1, 2 });
        await target.CompleteAsync();

        // The old content is gone and the unwritten tail is zeros
        Assert.Equal(new byte[] { 1, 2, 0, 0 }, File.ReadAllBytes(PathFor("a.bin")));
    }

    [Fact]
    public async Task AnEmptyWriteCreatesAZeroByteFile()
    {
        await using var target = new FileStreamTarget(PathFor("empty.bin"), 0);

        await target.WriteAsync(0, Array.Empty<byte>());
        await target.CompleteAsync();

        Assert.True(File.Exists(PathFor("empty.bin")));
        Assert.Equal(0, new FileInfo(PathFor("empty.bin")).Length);
    }

    [Fact]
    public async Task CompletingBeforeAnyWriteIsRefused()
    {
        await using var target = new FileStreamTarget(PathFor("a.bin"), 10);

        await Assert.ThrowsAsync<InvalidOperationException>(() => target.CompleteAsync());
    }

    [Fact]
    public async Task CompletingReleasesTheFile()
    {
        await using var target = new FileStreamTarget(PathFor("a.bin"), 3);
        await target.WriteAsync(0, new byte[] { 1, 2, 3 });
        await target.CompleteAsync();

        // Opening with no sharing only works once the target has let go of the file
        using var stream = new FileStream(PathFor("a.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(3, stream.Length);
    }

    [Fact]
    public async Task CancellingBeforeAnyWriteDoesNothing()
    {
        await using var target = new FileStreamTarget(PathFor("a.bin"), 10);

        await target.CancelAsync();

        Assert.False(File.Exists(PathFor("a.bin")));
    }

    [Fact]
    public async Task CancellingReleasesTheFileHandle()
    {
        await using var target = new FileStreamTarget(PathFor("a.bin"), 10);
        await target.WriteAsync(0, new byte[] { 1 });

        await target.CancelAsync();

        using var stream = new FileStream(PathFor("a.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task CancelAndDisposeCanBeRepeated()
    {
        var target = new FileStreamTarget(PathFor("a.bin"), 10);
        await target.WriteAsync(0, new byte[] { 1 });

        await target.CancelAsync();
        await target.CancelAsync();
        await target.DisposeAsync();
        await target.DisposeAsync();
    }

    [Fact]
    public async Task WritingIntoAMissingFolderThrows()
    {
        await using var target = new FileStreamTarget(PathFor(Path.Combine("missing", "a.bin")), 10);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => target.WriteAsync(0, new byte[] { 1 }));

        Assert.Null(target.FileStream);
    }

    [Fact]
    public async Task ProgressIsWrittenBytesOverTotalBytes()
    {
        await using var target = new FileStreamTarget(PathFor("a.bin"), 100);

        Assert.Equal(0, target.Progress);

        target.TotalBytes = 100;
        target.WrittenBytes = 25;

        Assert.Equal(0.25, target.Progress);
    }
}