using System.Security.Cryptography;
using System.Text;

namespace NexusPipeline;

public static class SecretStore
{
    private const string Prefix = "enc:";

    public static bool IsEncrypted(string stored)
    {
        return stored.StartsWith(Prefix, StringComparison.Ordinal);
    }

    public static string Encrypt(string plaintext)
    {
        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        byte[] protectedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedData);
    }

    public static bool TryDecrypt(string stored, out string? plaintext)
    {
        plaintext = null;
        if (string.IsNullOrWhiteSpace(stored))
        {
            plaintext = "";
            return true;
        }
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
