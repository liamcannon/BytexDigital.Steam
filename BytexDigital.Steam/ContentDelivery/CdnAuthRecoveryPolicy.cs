using System;
using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery;

internal enum CdnAuthRecoveryAction
{
    None,
    RetrySameServer,
    ExcludeServerForOperation
}

internal static class CdnAuthRecoveryPolicy
{
    internal static async Task<CdnAuthRecoveryAction> HandleForbiddenAsync(
        bool isCdnServer,
        bool isForbidden,
        bool retryingWithReplacementToken,
        bool canRetry,
        Func<CancellationToken, Task<bool>> refreshTokenAsync,
        Func<CancellationToken, Task> invalidateRejectedTokenAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refreshTokenAsync);
        ArgumentNullException.ThrowIfNull(invalidateRejectedTokenAsync);

        // Other servers and other HTTP errors use normal error handling.
        if (!isCdnServer || !isForbidden)
        {
            return CdnAuthRecoveryAction.None;
        }

        // The replacement token was rejected. Do not request another
        // token for this recovery sequence.
        if (retryingWithReplacementToken || !canRetry)
        {
            // The token was rejected, or there is no attempt left to
            // recover from this 403. Invalidate it if it is still cached.
            await invalidateRejectedTokenAsync(cancellationToken)
                .ConfigureAwait(false);

            return CdnAuthRecoveryAction.ExcludeServerForOperation;
        }

        // A usable cached replacement token also counts as success here.
        // A refresh exception is an authentication-recovery failure, not
        // evidence that the CDN host itself is unhealthy.
        try
        {
            var refreshed = await refreshTokenAsync(cancellationToken)
                .ConfigureAwait(false);

            return refreshed
                ? CdnAuthRecoveryAction.RetrySameServer
                : CdnAuthRecoveryAction.ExcludeServerForOperation;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return CdnAuthRecoveryAction.ExcludeServerForOperation;
        }
    }

    internal static CdnServerDisposition ApplyRecoveryDisposition(
        CdnServerDisposition currentDisposition,
        bool isForbidden,
        CdnAuthRecoveryAction recoveryAction,
        Action excludeServerForOperation)
    {
        ArgumentNullException.ThrowIfNull(excludeServerForOperation);

        if (isForbidden &&
            recoveryAction == CdnAuthRecoveryAction.ExcludeServerForOperation)
        {
            excludeServerForOperation();

            return CdnServerDisposition.Abandoned;
        }

        return currentDisposition;
    }
}
