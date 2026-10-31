using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AiTools.Configuration;

/// <summary>
/// 使用 Windows DPAPI（当前用户作用域）保护密钥。受保护的值以
/// <c>dpapi:</c> + Base64 的形式存储。不带此前缀的值按明文处理，
/// 以便手动编辑的设置文件仍可正常使用。
/// </summary>
/// <remarks>
/// 非 Windows 平台上 <see cref="Protect"/> 原样返回明文、<see cref="Unprotect"/> 返回空字符串：
/// 设置文件仍可读写，只是密钥不受平台级保护，调用方应自行限制文件访问权限。
/// </remarks>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";

#if NET5_0_OR_GREATER
    // SupportedOSPlatformGuard 让平台分析器理解分支，避免 CA1416 误报。
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    private static bool IsWindows => OperatingSystem.IsWindows();
#else
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        if (!IsWindows)
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

        if (!IsWindows)
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

/// <summary>
/// 不做任何加密的保护器。用于测试、非 Windows 平台或宿主自行管理密钥的场景；
/// 它把 API Key 以明文写入设置文件。
/// </summary>
public sealed class PassThroughSecretProtector : ISecretProtector
{
    /// <inheritdoc />
    public string Protect(string plaintext) => plaintext ?? string.Empty;

    /// <inheritdoc />
    public string Unprotect(string protectedText) => protectedText ?? string.Empty;
}
