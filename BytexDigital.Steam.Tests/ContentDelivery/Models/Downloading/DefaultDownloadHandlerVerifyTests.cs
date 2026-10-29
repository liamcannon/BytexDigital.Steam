using System.Collections.Concurrent;
using System.Security.Cryptography;
using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.ContentDelivery.Enumerations;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.ContentDelivery.Models;
using BytexDigital.Steam.ContentDelivery.Models.Downloading;
using BytexDigital.Steam.Core;
using Microsoft.Extensions.Logging;

namespace BytexDigital.Steam.Tests.ContentDelivery.Models.Downloading;

public class DefaultDownloadHandlerVerifyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bytex_verify_{Guid.NewGuid():N}");
    private readonly List<SteamClient> _clients = new();
    private readonly List<DefaultDownloadHandler> _handlers = new();

    public DefaultDownloadHandlerVerifyTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
            handler.Dispose();

        foreach (var client in _clients)
            client.Shutdown();

        Directory.Delete(_dir, true);
    }

    // A client that is never connected is enough: verification only reads the manifest and the disk
    private DefaultDownloadHandler CreateHandler(params ManifestFile[] files)
    {
        var steamClient = new SteamClient(
            SteamCredentials.Anonymous,
            new ConsoleSteamAuthenticator("tests"));
        _clients.Add(steamClient);

        var contentClient = new SteamContentClient(steamClient);
        var manifest = new Manifest(files.ToList(), 1u, 1ul, DateTime.UtcNow, 0, 0);

        var handler = new DefaultDownloadHandler(contentClient, manifest, 1u, 1u);
        _handlers.Add(handler);

        return handler;
    }

    private static ManifestFile FileEntry(string name, byte[] content, ManifestFileFlag flags = default)
    {
        var chunk = new ManifestFileChunkHeader(
            new byte[20], 0, 0, (uint) content.Length, (uint) content.Length);

        return new ManifestFile(
            name,
            new List<ManifestFileChunkHeader> { chunk },
            flags,
            (ulong) content.Length,
            SHA1.HashData(content));
    }

    private sealed record VerifyResult(
        List<FileVerifiedArgs> Files,
        VerificationCompletedArgs Completed);

    private async Task<VerifyResult> RunVerify(
        DefaultDownloadHandler handler,
        Func<ManifestFile, bool>? condition = null)
    {
        var files = new ConcurrentBag<FileVerifiedArgs>();
        var completed = new TaskCompletionSource<VerificationCompletedArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        handler.FileVerified += (_, args) => files.Add(args);
        handler.VerificationCompleted += (_, args) => completed.TrySetResult(args);

        await handler.SetupAsync(_dir, condition ?? (_ => true));
        await handler.VerifyAsync();

        // VerificationCompleted is raised in the background, so wait for it
        var done = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        return new VerifyResult(files.ToList(), done);
    }

    [Fact]
    public async Task MatchingFileOnDiskIsNotQueued()
    {
        var content = "hello"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_dir, "a.txt"), content);
        var handler = CreateHandler(FileEntry("a.txt", content));

        var result = await RunVerify(handler);

        Assert.Equal(DownloadHandlerStateEnum.Verified, handler.State);
        Assert.Empty(result.Completed.QueuedFiles);
        Assert.False(Assert.Single(result.Files).RequiresDownload);
    }

    [Fact]
    public async Task MissingFileIsQueued()
    {
        var handler = CreateHandler(FileEntry("a.txt", "hello"u8.ToArray()));

        var result = await RunVerify(handler);

        Assert.Equal("a.txt", Assert.Single(result.Completed.QueuedFiles).FileName);
        Assert.True(Assert.Single(result.Files).RequiresDownload);
    }

    [Fact]
    public async Task ChangedFileOfTheSameSizeIsDeletedAndQueued()
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllBytes(path, "hxllo"u8.ToArray());
        var handler = CreateHandler(FileEntry("a.txt", "hello"u8.ToArray()));

        var result = await RunVerify(handler);

        Assert.Equal("a.txt", Assert.Single(result.Completed.QueuedFiles).FileName);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FileOfTheWrongSizeIsDeletedAndQueued()
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllBytes(path, "hi"u8.ToArray());
        var handler = CreateHandler(FileEntry("a.txt", "hello"u8.ToArray()));

        var result = await RunVerify(handler);

        Assert.Equal("a.txt", Assert.Single(result.Completed.QueuedFiles).FileName);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DirectoriesFilteredFilesAndDuplicatesAreSkipped()
    {
        var handler = CreateHandler(
            FileEntry("wanted.txt", "a"u8.ToArray()),
            FileEntry("wanted.txt", "a"u8.ToArray()),
            FileEntry("folder", Array.Empty<byte>(), ManifestFileFlag.Directory),
            FileEntry("skipped.txt", "b"u8.ToArray()));

        var result = await RunVerify(handler, file => file.FileName != "skipped.txt");

        Assert.Equal("wanted.txt", Assert.Single(result.Completed.QueuedFiles).FileName);
        Assert.Single(result.Files);
    }

    [Fact]
    public async Task EmptyManifestVerifiesWithNothingQueued()
    {
        var handler = CreateHandler();

        var result = await RunVerify(handler);

        Assert.Equal(DownloadHandlerStateEnum.Verified, handler.State);
        Assert.Empty(result.Completed.QueuedFiles);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task NameThatEscapesTheDirectoryFailsAndWritesNothing()
    {
        var handler = CreateHandler(FileEntry("../evil.txt", "x"u8.ToArray()));
        await handler.SetupAsync(_dir, _ => true);

        await Assert.ThrowsAsync<InvalidDataException>(() => handler.VerifyAsync());

        Assert.Equal(DownloadHandlerStateEnum.Failed, handler.State);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_dir, "..", "evil.txt"))));
    }

    [Fact]
    public async Task VerifyBeforeSetupIsRefused()
    {
        var handler = CreateHandler();

        await Assert.ThrowsAsync<DownloadHandlerStateMismatchException>(() => handler.VerifyAsync());
    }

    [Fact]
    public async Task ThrowingEventHandlerIsLoggedAndDoesNotStopVerification()
    {
        var logger = new CapturingLogger();
        var handler = CreateHandler(FileEntry("a.txt", "hello"u8.ToArray()));
        handler.Logger = logger;
        handler.FileVerified += (_, _) => throw new InvalidOperationException("boom");

        await RunVerify(handler);

        Assert.Equal(DownloadHandlerStateEnum.Verified, handler.State);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentBag<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, exception));
        }
    }
}