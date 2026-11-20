# BytexDigital.Steam

A .NET wrapper around [SteamKit2](https://github.com/SteamRE/SteamKit) designed to simplify interaction with Steam APIs, particularly application content, depot information and Steam Workshop downloads.

The library provides functionality for Steam authentication, content retrieval, manifest handling and downloading, with built-in management of CDN servers, download verification and recovery from common network failures.

> [!WARNING]
> **This library is used by FASTER and may change at any time** Public APIs and functionality may change significantly between versions. Breaking changes should be expected.

## Requirements

- .NET 10.0 or later.
- Internet access to Steam services and content delivery servers.
- A Steam account with appropriate permissions for the content being accessed, unless anonymous access is supported.

## Authentication

BytexDigital.Steam uses `SteamCredentials` and `SteamAuthenticator` to manage Steam authentication.

### Steam Credentials

Authenticated connections require a username and password:

```csharp
var credentials = new SteamCredentials(
    "username",
    "password");
```

For services that support anonymous access, you can use:

```csharp
var credentials = SteamCredentials.Anonymous;
```

### Steam Authenticator

A `SteamAuthenticator` implementation handles the authentication process, including:

- Steam Guard mobile authentication codes.
- Email authentication codes.
- Mobile authentication confirmations.
- Access-token retrieval and persistence.
- Guard Data retrieval and persistence.

The library includes `ConsoleSteamAuthenticator` as an example implementation. It prompts for authentication codes through the console.

```csharp
var authenticator = new ConsoleSteamAuthenticator(
    "my-account");
```

A persistence directory can also be provided:

```csharp
var authenticator = new ConsoleSteamAuthenticator(
    "my-account",
    ".\\auth");
```

**Security considerations:** The built-in `ConsoleSteamAuthenticator` stores persisted access tokens and Guard Data as plain-text files. It is intended as an example implementation rather than secure production storage.

Applications handling real Steam credentials should implement their own `SteamAuthenticator` using an appropriate secure storage mechanism.

### Custom Authenticators

Applications can implement their own authentication behaviour by inheriting from `SteamAuthenticator`.

Implementations must provide methods for:

- Retrieving Steam Guard and email authentication codes.
- Handling mobile authentication confirmations.
- Retrieving and persisting access tokens.
- Retrieving and persisting Guard Data.

**Access-token cleanup:** Passing an empty string to `PersistAccessTokenAsync()` represents a request to clear the previously persisted access token.

Custom implementations must support this behaviour so the Steam client can automatically remove rejected access tokens from persistent storage.

### Automatic Access Token Recovery

The Steam client automatically detects when Steam rejects a previously persisted access token.

The following Steam authentication results trigger automatic token cleanup when the login attempt actually used an access token:

- `InvalidSignature`
- `AccessDenied`
- `Expired`
- `Revoked`

The rejected token is cleared from the authenticator's in-memory and persistent storage, allowing subsequent authentication attempts to use the username and password flow.

Unrelated authentication failures, such as `InvalidPassword`, connection failures and timeouts, do not trigger token cleanup.

Steam Guard Data is preserved during access-token cleanup.

## Getting Started

### Connecting to Steam

Create a `SteamClient` using your credentials and authenticator, then establish a connection:

```csharp
using BytexDigital.Steam.Core;

var credentials = new SteamCredentials(
    "username",
    "password");

var authenticator = new ConsoleSteamAuthenticator(
    "my-account");

using var steamClient = new SteamClient(
    credentials,
    authenticator);

await steamClient.ConnectAsync();
```

`ConnectAsync()` establishes the connection and completes the authentication process before returning.

The client exposes properties such as `IsConnected`, `IsFaulted` and `State` to help monitor its lifecycle.

### Creating a Steam Content Client

Once connected, create a `SteamContentClient` to access Steam content functionality:

```csharp
using BytexDigital.Steam.ContentDelivery;

await using var contentClient = new SteamContentClient(steamClient);
```

The content client provides functionality for retrieving application information, depot information, manifests and downloadable content.

### Retrieving Application and Depot Information

You can retrieve available branches and depots for an application:

```csharp
using BytexDigital.Steam.Core.Structs;

var appId = new AppId(123456);

var branches = await contentClient.GetBranchesAsync(appId);

var depots = await contentClient.GetDepotsAsync(
    appId,
    "public");

foreach (var depot in depots)
{
    Console.WriteLine($"Depot: {depot.Id} - {depot.Name}");
}
```

You can also retrieve a specific depot manifest ID:

```csharp
var manifestId = await contentClient.GetDepotManifestIdAsync(
    appId,
    depotId,
    "public");
```

Password-protected branches require the appropriate branch password.

## Content Downloads

Downloads are managed through `IDownloadHandler` implementations.

The standard download workflow consists of three stages:

1. **Setup:** Configure the download directory and file selection criteria.
2. **Verification:** Compare existing local files against the Steam manifest and identify files that require downloading.
3. **Download:** Retrieve the required files from Steam's content delivery network.

### Downloading Application Content

The following example retrieves the content for an application's public branch and downloads the selected files.

```csharp
using System;
using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Structs;

var credentials = new SteamCredentials(
    "username",
    "password");

var authenticator = new ConsoleSteamAuthenticator(
    "my-account");

using var steamClient = new SteamClient(
    credentials,
    authenticator);

await steamClient.ConnectAsync();

await using var contentClient = new SteamContentClient(steamClient);

var appId = new AppId(123456);

await using var downloadHandler = await contentClient.GetAppDataAsync(
    appId,
    branch: "public",
    skipInaccessibleDepots: false);

downloadHandler.FileVerified += (_, args) =>
{
    Console.WriteLine(
        $"{args.ManifestFile.FileName}: " +
        (args.RequiresDownload ? "Download required" : "Already up to date"));
};

downloadHandler.FileDownloaded += (_, file) =>
{
    Console.WriteLine($"Downloaded: {file.FileName}");
};

downloadHandler.VerificationCompleted += (_, args) =>
{
    Console.WriteLine(
        $"Verification completed. {args.QueuedFiles.Count} files require downloading.");
};

downloadHandler.DownloadComplete += (_, _) =>
{
    Console.WriteLine("Download completed.");
};

await downloadHandler.SetupAsync(
    ".\\downloads",
    file => true);

await downloadHandler.VerifyAsync();

await downloadHandler.DownloadAsync();
```

The example downloads all eligible files for the selected branch.

`skipInaccessibleDepots` controls whether depots that cannot be accessed are skipped or cause the operation to fail.

### Downloading a Specific Depot

You can restrict a download to a particular depot:

```csharp
var downloadHandler = await contentClient.GetAppDataAsync(
    appId,
    new DepotId(123456),
    branch: "public");
```

A specific manifest can also be supplied:

```csharp
var downloadHandler = await contentClient.GetAppDataAsync(
    appId,
    new DepotId(123456),
    new ManifestId(123456789));
```

Both methods return an `IDownloadHandler` and use the same setup, verification and download workflow.

### Downloading Steam Workshop Content

Workshop items can be retrieved using their published file ID:

```csharp
var publishedFileId = new PublishedFileId(123456789);

await using var downloadHandler =
    await contentClient.GetPublishedFileDataAsync(publishedFileId);

await downloadHandler.SetupAsync(
    ".\\workshop-downloads",
    file => true);

await downloadHandler.VerifyAsync();

await downloadHandler.DownloadAsync();
```

The library retrieves the published file information and determines the appropriate download method.

Workshop downloads may use either Steam content delivery or a legacy download URL, depending on the published file.

### Filtering Files

A condition can be supplied to `SetupAsync()` to control which files are included in a download.

For example, to only download executable files:

```csharp
await downloadHandler.SetupAsync(
    ".\\downloads",
    file => file.FileName.EndsWith(
        ".exe",
        StringComparison.OrdinalIgnoreCase));
```

Files that do not match the condition are excluded from verification and downloading.

## Download Progress and Events

Download handlers expose progress information and events that can be used to integrate downloads into an application interface or logging system.

### Progress Properties

| Property | Description |
|---|---|
| `TotalProgress` | Overall download progress, represented as a value between 0 and 1. |
| `TotalFileCount` | Number of files included in the download. |
| `TotalFileSize` | Total size of the files included in the download. |
| `State` | Current download handler state. |

For example, displaying overall progress:

```csharp
Console.WriteLine(
    $"Progress: {downloadHandler.TotalProgress * 100:0.00}%");
```

### Download Events

| Event | Description |
|---|---|
| `FileVerified` | Raised when an individual file has been verified. |
| `VerificationCompleted` | Raised when the verification stage has completed. |
| `FileDownloaded` | Raised when an individual file has finished downloading. |
| `DownloadComplete` | Raised when the complete download operation has finished. |

`FileVerified` provides a `FileVerifiedArgs` object containing the manifest file and whether it requires downloading.

`VerificationCompleted` provides a `VerificationCompletedArgs` object containing the files queued for download.

## Error Handling and Cancellation

Steam client, content retrieval and download operations support cancellation through `CancellationToken` parameters.

For example:

```csharp
using var cancellationTokenSource = new CancellationTokenSource();

var cancellationToken = cancellationTokenSource.Token;

var downloadHandler = await contentClient.GetAppDataAsync(
    appId,
    branch: "public",
    cancellationToken: cancellationToken);

await downloadHandler.SetupAsync(
    ".\\downloads",
    file => true,
    cancellationToken);

await downloadHandler.VerifyAsync(cancellationToken);

await downloadHandler.DownloadAsync(cancellationToken);
```

Cancellation can be requested through the associated `CancellationTokenSource`.

The library also provides dedicated exceptions for common Steam and content delivery failures, including:

- Authentication failures.
- Missing or inaccessible applications and depots.
- Invalid branch passwords.
- Missing manifests.
- Unavailable content servers.
- Download and verification failures.
- Invalid download handler states.
- Missing or unavailable Workshop content.

Exceptions are propagated to the caller so applications can implement their own error handling and recovery strategies.

## Logging

`SteamContentClient` exposes an optional `ILogger` property for logging content operations.

A compatible logger can be supplied before creating download handlers:

```csharp
contentClient.Logger = logger;
```

The standard content download handlers inherit the logger configured on the content client.

The library uses structured logging for relevant operations, including download activity, CDN requests, retries, authentication recovery and failures.

The logger can be supplied through any compatible `ILogger` implementation from `Microsoft.Extensions.Logging.Abstractions`.

## Resource Management

Steam clients, content clients and download handlers manage resources that should be disposed of when they are no longer required.

| Component | Disposal |
|---|---|
| `SteamClient` | `IDisposable` |
| `SteamContentClient` | `IAsyncDisposable` |
| `IDownloadHandler` | `IDisposable` and `IAsyncDisposable` |

Prefer asynchronous disposal for content clients and download handlers:

```csharp
using var steamClient = new SteamClient(
    credentials,
    authenticator);

await steamClient.ConnectAsync();

await using var contentClient = new SteamContentClient(steamClient);

await using var downloadHandler =
    await contentClient.GetAppDataAsync(appId, branch: "public");
```

Resources should be disposed in reverse order of creation:

1. Download handlers.
2. Steam content client.
3. Steam client.

The Steam client cannot be reused after shutdown or disposal. Create a new instance when another connection is required.

## Breaking Changes and Migration Notes

This update includes changes to the public API and internal implementation.

Existing consumers should review the following areas when upgrading:

- **Constructors:** Some previously available Steam client constructor overloads have been removed or changed.
- **Public types:** Several previously public types and extensions have been removed.
- **Exception inheritance:** Some exception types no longer inherit from their previous base classes. In particular, `SteamDepotKeyNotRetrievableException` no longer derives from `SteamAccessDeniedException`.
- **Download handling:** Download handler lifecycle and disposal behaviour have been improved.
- **Namespaces and organisation:** Exception files have been reorganised to better reflect their existing namespaces. Folder moves do not necessarily represent namespace changes.

Applications relying on removed types, constructors or exception inheritance may require code changes.

As this library is still in early beta, consumers should review API changes when upgrading between versions.

## Development and Testing

The repository includes a dedicated xUnit test project covering:

- Steam client lifecycle and authentication.
- Access-token persistence and rejection handling.
- CDN server management and recovery.
- CDN authentication token caching.
- Download verification and file handling.
- Multi-depot downloads.
- Cancellation and disposal behaviour.

### Building the Solution

```bash
dotnet build BytexDigital.Steam.sln
```

### Running Automated Tests

```bash
dotnet test BytexDigital.Steam.sln
```

## Examples

The repository includes a test client demonstrating Steam authentication and content downloads:

[BytexDigital.Steam Test Client](BytexDigital.Steam.TestClient/Program.cs)

## License

This project is distributed under the license included in [LICENSE.md](LICENSE.md).
