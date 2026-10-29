using System.Security.Cryptography;
using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.ContentDelivery.Models;

namespace BytexDigital.Steam.Tests.ContentDelivery;

public class DecryptManifestIdTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte) i).ToArray();

    private static DepotEncryptedManifest Encrypted(string hex) =>
        new DepotEncryptedManifest(1u, 2u, "beta", hex, DepotEncryptedManifest.EncryptionVersion.V2);

    private static string EncryptToHex(ulong manifestId)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor(Key, null);
        var plain = BitConverter.GetBytes(manifestId);

        return Convert.ToHexString(encryptor.TransformFinalBlock(plain, 0, plain.Length));
    }

    [Fact]
    public void CorrectKeyRoundTrips()
    {
        var result = SteamContentClient.DecryptManifestId(Encrypted(EncryptToHex(123456789UL)), Key);

        ulong id = result.ManifestId;

        Assert.Equal(123456789UL, id);
        Assert.Equal("beta", result.BranchName);
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("ABC")]
    [InlineData("")]
    public void MalformedIdsAreReportedAsInvalidPassword(string hex)
    {
        Assert.Throws<SteamInvalidBranchPasswordException>(
            () => SteamContentClient.DecryptManifestId(Encrypted(hex), Key));
    }
}