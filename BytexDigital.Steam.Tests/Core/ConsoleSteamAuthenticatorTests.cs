using BytexDigital.Steam.Core;

namespace BytexDigital.Steam.Tests.Core;

public class ConsoleSteamAuthenticatorTests
{
    private const string StorageName = "test-account";

    [Fact]
    public async Task PersistAccessTokenStoresTokenInMemoryAndOnDisk()
    {
        using var directory = new TemporaryDirectory();
        var authenticator = new ConsoleSteamAuthenticator(StorageName, directory.DirectoryPath);
        const string token = "test-access-token";

        await authenticator.PersistAccessTokenAsync(token);

        Assert.Equal(token, authenticator.AccessToken);
        Assert.Equal(token, await authenticator.GetAccessTokenAsync());
        Assert.True(File.Exists(GetAccessTokenPath(directory.DirectoryPath)));
    }

    [Fact]
    public async Task ClearAccessTokenRemovesTokenFromMemoryAndDisk()
    {
        using var directory = new TemporaryDirectory();
        var authenticator = new ConsoleSteamAuthenticator(StorageName, directory.DirectoryPath);
        const string token = "test-access-token";

        await authenticator.PersistAccessTokenAsync(token);

        await authenticator.ClearAccessTokenAsync();

        Assert.Empty(authenticator.AccessToken);
        Assert.False(File.Exists(GetAccessTokenPath(directory.DirectoryPath)));
    }

    [Fact]
    public async Task ClearAccessTokenSucceedsWhenPersistedFileIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var authenticator = new ConsoleSteamAuthenticator(StorageName, directory.DirectoryPath);

        await authenticator.PersistAccessTokenAsync("test-access-token");

        File.Delete(GetAccessTokenPath(directory.DirectoryPath));

        await authenticator.ClearAccessTokenAsync();

        Assert.Empty(authenticator.AccessToken);
        Assert.False(File.Exists(GetAccessTokenPath(directory.DirectoryPath)));
    }

    [Fact]
    public async Task ClearAccessTokenWithoutPersistenceDirectoryClearsMemory()
    {
        var authenticator = new ConsoleSteamAuthenticator(StorageName);

        await authenticator.PersistAccessTokenAsync("test-access-token");

        await authenticator.ClearAccessTokenAsync();

        Assert.Empty(authenticator.AccessToken);
    }

    [Fact]
    public async Task ClearAccessTokenLeavesGuardDataUntouched()
    {
        using var directory = new TemporaryDirectory();
        var authenticator = new ConsoleSteamAuthenticator(StorageName, directory.DirectoryPath);
        const string guardData = "test-guard-data";

        await authenticator.PersistAccessTokenAsync("test-access-token");
        await authenticator.PersistGuardDataAsync(guardData);

        await authenticator.ClearAccessTokenAsync();

        Assert.Empty(authenticator.AccessToken);
        Assert.Equal(guardData, authenticator.GuardData);
        Assert.Equal(guardData, await authenticator.GetGuardDataAsync());
        Assert.True(File.Exists(GetGuardDataPath(directory.DirectoryPath)));
    }

    [Fact]
    public async Task CancelledClearAccessTokenPreservesExistingToken()
    {
        using var directory = new TemporaryDirectory();
        var authenticator = new ConsoleSteamAuthenticator(StorageName, directory.DirectoryPath);
        const string token = "test-access-token";

        await authenticator.PersistAccessTokenAsync(token);

        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => authenticator.ClearAccessTokenAsync(cancellationTokenSource.Token));

        Assert.Equal(token, authenticator.AccessToken);
        Assert.Equal(token, await authenticator.GetAccessTokenAsync());
        Assert.True(File.Exists(GetAccessTokenPath(directory.DirectoryPath)));
    }

    private static string GetAccessTokenPath(string directoryPath)
    {
        return Path.Combine(directoryPath, $"{StorageName}_accesstoken");
    }

    private static string GetGuardDataPath(string directoryPath)
    {
        return Path.Combine(directoryPath, $"{StorageName}_guarddata");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(),
            $"BytexDigital.Steam.Tests_{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}