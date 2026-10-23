using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AiTools.Wpf.Configuration;

/// <summary>
/// 使用 Windows DPAPI（当前用户作用域）保护密钥。受保护的值以
/// <c>dpapi:</c> + Base64 的形式存储。不带此前缀的值按明文处理，
/// 以便手动编辑的设置文件仍可正常使用。
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows())
        {
            return plaintext;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        catch (CryptographicException)
        {
            return plaintext;
        }
    }

    /// <inheritdoc />
    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return string.Empty;
        }

        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return protectedText;
        }

        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            var bytes = Convert.FromBase64String(protectedText.Substring(Prefix.Length));
            var plainBytes = ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
        {
            return string.Empty;
        }
    }
}
