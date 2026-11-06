using System.Net;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.ContentDelivery.Models;
using BytexDigital.Steam.ContentDelivery.Models.Downloading;

namespace BytexDigital.Steam.Tests.ContentDelivery.Models.Downloading;

public class LegacyDownloadHandlerTests : IDisposable
{
    private const string Url = "https://workshop.example.com/files/mod.zip";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bytex_legacy_{Guid.NewGuid():N}");
    private readonly List<HttpClient> _clients = new();

    public LegacyDownloadHandlerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        Directory.Delete(_dir, true);
    }

    private LegacyDownloadHandler Create(StubHandler stub, string fileName = "mod.zip")
    {
        var client = new HttpClient(stub);
        _clients.Add(client);
        return new LegacyDownloadHandler(Url, fileName, client);
    }

    private static StubHandler Returns(byte[] content) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));

    [Fact]
    public async Task SetupMakesTheHandlerReadyToDownload()
    {
        var handler = Create(Returns(Array.Empty<byte>()));

        await handler.SetupAsync(_dir, _ => true);

        Assert.Equal(DownloadHandlerStateEnum.Verified, handler.State);
        Assert.Equal(_dir, handler.DownloadDirectory);
    }

    [Fact]
    public async Task SettingUpTwiceIsRefused()
    {
        var handler = Create(Returns(Array.Empty<byte>()));
        await handler.SetupAsync(_dir, _ => true);

        await Assert.ThrowsAsync<DownloadHandlerStateMismatchException>(() => handler.SetupAsync(_dir, _ => true));
    }

    [Fact]
    public async Task DownloadingBeforeSetupIsRefusedAndMakesNoRequest()
    {
        var stub = Returns("x"u8.ToArray());
        var handler = Create(stub);

        await Assert.ThrowsAsync<DownloadHandlerStateMismatchException>(() => handler.DownloadAsync());

        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task SuccessfulDownloadWritesTheFileAndCompletes()
    {
        var stub = Returns("hello"u8.ToArray());
        var handler = Create(stub);
        var downloaded = new TaskCompletionSource<ManifestFile>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.FileDownloaded += (_, file) => downloaded.TrySetResult(file);
        handler.DownloadComplete += (_, _) => completed.TrySetResult();
        await handler.SetupAsync(_dir, _ => true);

        await handler.DownloadAsync();

        Assert.Equal("hello"u8.ToArray(), File.ReadAllBytes(Path.Combine(_dir, "mod.zip")));
        Assert.Equal(DownloadHandlerStateEnum.Downloaded, handler.State);
        Assert.Equal(1d, handler.TotalProgress);
        Assert.Equal(5UL, handler.TotalFileSize);
        Assert.Equal(new Uri(Url), stub.LastUri);

        var file = await downloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("mod.zip", file.FileName);
        Assert.Equal(5UL, file.TotalSize);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ANestedFileNameCreatesItsFolder()
    {
        var handler = Create(Returns("x"u8.ToArray()), fileName: "sub/dir/mod.zip");
        await handler.SetupAsync(_dir, _ => true);

        await handler.DownloadAsync();

        Assert.True(File.Exists(Path.Combine(_dir, "sub", "dir", "mod.zip")));
    }

    [Fact]
    public async Task AnExistingFileIsOverwritten()
    {
        File.WriteAllBytes(Path.Combine(_dir, "mod.zip"), Enumerable.Repeat((byte) 9, 50).ToArray());
        var handler = Create(Returns("new"u8.ToArray()));
        await handler.SetupAsync(_dir, _ => true);

        await handler.DownloadAsync();

        Assert.Equal("new"u8.ToArray(), File.ReadAllBytes(Path.Combine(_dir, "mod.zip")));
    }

    [Fact]
    public async Task ANameThatEscapesTheDirectoryFailsBeforeAnyRequest()
    {
        var stub = Returns("x"u8.ToArray());
        var handler = Create(stub, fileName: "../evil.zip");
        await handler.SetupAsync(_dir, _ => true);

        await Assert.ThrowsAsync<InvalidDataException>(() => handler.DownloadAsync());

        Assert.Equal(DownloadHandlerStateEnum.Failed, handler.State);
        Assert.Equal(0, stub.Calls);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_dir, "..", "evil.zip"))));
    }

    [Fact]
    public async Task AnHttpErrorFailsTheDownloadAndLeavesNoFile()
    {
        var stub = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var handler = Create(stub);
        await handler.SetupAsync(_dir, _ => true);

        await Assert.ThrowsAsync<HttpRequestException>(() => handler.DownloadAsync());

        Assert.Equal(DownloadHandlerStateEnum.Failed, handler.State);
        Assert.False(File.Exists(Path.Combine(_dir, "mod.zip")));
    }

    [Fact]
    public async Task ACancelledDownloadFailsAndLeavesNoFile()
    {
        var stub = new StubHandler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("x"u8.ToArray()) });
        });
        var handler = Create(stub);
        await handler.SetupAsync(_dir, _ => true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.DownloadAsync(cancellation.Token));

        Assert.Equal(DownloadHandlerStateEnum.Failed, handler.State);
        Assert.False(File.Exists(Path.Combine(_dir, "mod.zip")));
    }

    [Fact]
    public async Task AFailureWhileReadingTheBodyDeletesThePartialFile()
    {
        var stub = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) }));
        var handler = Create(stub);
        await handler.SetupAsync(_dir, _ => true);

        await Assert.ThrowsAnyAsync<IOException>(() => handler.DownloadAsync());

        Assert.Equal(DownloadHandlerStateEnum.Failed, handler.State);
        Assert.False(File.Exists(Path.Combine(_dir, "mod.zip")));
    }

    [Fact]
    public async Task DisposeCanBeCalledMoreThanOnce()
    {
        var handler = Create(Returns(Array.Empty<byte>()));

        handler.Dispose();
        await handler.DisposeAsync();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        private int _calls;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public int Calls => Volatile.Read(ref _calls);
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastUri = request.RequestUri;
            return _respond(request, cancellationToken);
        }
    }

    // Serves four bytes, then fails, like a connection that drops mid-download
    private sealed class FailingStream : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(ReadCore(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadCore(Span<byte> buffer)
        {
            if (_served)
            {
                throw new IOException("The connection dropped.");
            }

            _served = true;
            "abcd"u8.CopyTo(buffer);
            return 4;
        }
    }

    [Fact]
    public async Task AFileTheConditionRejectsIsNeitherRequestedNorWritten()
    {
        var stub = Returns("x"u8.ToArray());
        var handler = Create(stub);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileRaised = false;
        handler.FileDownloaded += (_, _) => fileRaised = true;
        handler.DownloadComplete += (_, _) => completed.TrySetResult();
        await handler.SetupAsync(_dir, _ => false);

        await handler.DownloadAsync();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, stub.Calls);
        Assert.False(File.Exists(Path.Combine(_dir, "mod.zip")));
        Assert.Equal(DownloadHandlerStateEnum.Downloaded, handler.State);
        Assert.Equal(1d, handler.TotalProgress);
        Assert.False(fileRaised);
    }

    [Fact]
    public async Task TheConditionIsGivenTheFileName()
    {
        ManifestFile? seen = null;
        var handler = Create(Returns("x"u8.ToArray()));
        await handler.SetupAsync(_dir, file =>
        {
            seen = file;
            return true;
        });

        await handler.DownloadAsync();

        Assert.Equal("mod.zip", seen?.FileName);
    }
}