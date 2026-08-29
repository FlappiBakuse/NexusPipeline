using System.Security.Cryptography;
using System.Text;

namespace NexusPipeline.Persistence;

internal static class SecretStore
{
    private const string Prefix = "enc:";
#if NEXUS_TEST_HOST
    // The ordinary-permission Test Host can run under a Windows token whose
    // user profile is not loaded. Keep its fallback storage isolated behind a
    // test-only prefix so production data continues to use DPAPI.
    private const string TestFallbackPrefix = "enc:test:";
#endif

    public static bool IsEncrypted(string stored)
    {
        return stored.StartsWith(Prefix, StringComparison.Ordinal);
    }

    public static string Encrypt(string plaintext)
    {
        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            byte[] protectedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedData);
        }
#if NEXUS_TEST_HOST
        catch (CryptographicException)
        {
            // Test Host state lives under tests/.artifacts and is removed by
            // the runner. Base64 keeps the test-only fallback out of the
            // plaintext path while allowing ordinary-permission UI tests to
            // exercise save/read behavior when DPAPI has no profile.
            return TestFallbackPrefix + Convert.ToBase64String(data);
        }
#else
        catch (CryptographicException)
        {
            throw;
        }
#endif
    }

    public static bool TryDecrypt(string stored, out string? plaintext)
    {
        plaintext = null;
        if (string.IsNullOrWhiteSpace(stored))
        {
            plaintext = "";
            return true;
        }
#if NEXUS_TEST_HOST
        if (stored.StartsWith(TestFallbackPrefix, StringComparison.Ordinal))
        {
            try
            {
                plaintext = Encoding.UTF8.GetString(Convert.FromBase64String(stored[TestFallbackPrefix.Length..]));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
#endif
        if (!IsEncrypted(stored))
        {
            plaintext = stored;
            return true;
        }
        try
        {
            byte[] data = Convert.FromBase64String(stored[Prefix.Length..]);
            byte[] unprotected = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            plaintext = Encoding.UTF8.GetString(unprotected);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
