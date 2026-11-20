using System;
using BytexDigital.Steam.Core.Structs;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamInvalidBranchPasswordException : Exception
    {
        // The password itself is deliberately not part of the message, because messages end up in the console and log
        public SteamInvalidBranchPasswordException(
            AppId appId,
            DepotId depotId,
            string branch,
            Exception? innerException = null) : base("Invalid password " +
                                                    $"for branch = {branch} of app id = {appId}, depot id = {depotId}{(innerException != null ? ". View the inner exception for more details." : "")}",
            innerException)
        {
        }

        // For an encrypted manifest id that could not be read at all. That says nothing about the password.
        public SteamInvalidBranchPasswordException(
            AppId appId,
            DepotId depotId,
            string branch,
            string reason,
            Exception? innerException) : base(
            $"The encrypted manifest id for branch = {branch} of app id = {appId}, depot id = {depotId} could not be read: {reason}",
            innerException)
        {
        }
    }
}