using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Wpf.Configuration;

/// <summary>当 AiTools.Wpf 无法完成某项操作时抛出。</summary>
public sealed class AiToolsWpfException : Exception
{
    /// <summary>使用消息文本创建异常。</summary>
    public AiToolsWpfException(string message)
        : base(message)
    {
    }

    /// <summary>使用消息文本和内部异常创建异常。</summary>
    public AiToolsWpfException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 根据服务商配置创建 chat 客户端。实现此接口可接入自定义 provider、
/// 代理、日志记录或密钥轮换。
/// </summary>
public interface IProviderFactory
{
    /// <summary>为给定的服务商配置创建 chat 客户端。</summary>
    /// <param name="provider">服务商配置，包含已解密的 API Key。</param>
    /// <exception cref="AiToolsWpfException">服务商配置不完整或不受支持。</exception>
    IChatClient CreateChatClient(ProviderConfig provider);
}

/// <summary>
/// 根据服务商配置创建模型列表客户端。实现此接口可让设置页的「获取模型列表」
/// 通过自定义网关、缓存或别的来源返回可用模型。
/// </summary>
public interface IProviderModelCatalogFactory
{
    /// <summary>为给定的服务商配置创建模型列表客户端。</summary>
    /// <param name="provider">服务商配置，包含已解密的 API Key。</param>
    /// <exception cref="AiToolsWpfException">服务商配置不完整或不受支持。</exception>
    IModelCatalog CreateModelCatalog(ProviderConfig provider);
}

/// <summary>持久化 <see cref="AppSettings"/>。实现此接口可将设置存储到数据库或自定义位置。</summary>
public interface ISettingsStore
{
    /// <summary>加载设置。尚未保存过任何设置时，返回 <see cref="AppSettings.Version"/> 为零的 <see cref="AppSettings"/>。</summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存设置。</summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>静态（落盘时）保护 API Key。实现此接口可自定义加密方案。</summary>
public interface ISecretProtector
{
    /// <summary>加密明文密钥。</summary>
    string Protect(string plaintext);

    /// <summary>解密受保护的密钥。无法解密时返回空字符串。</summary>
    string Unprotect(string protectedText);
}

/// <summary>
/// 提供对话页向 model 暴露的 Tool。实现此接口可注册宿主功能，
/// 例如文件搜索或业务 API。
/// </summary>
public interface IChatToolProvider
{
    /// <summary>获取要向 model 提供的 Tool 定义。</summary>
    IReadOnlyList<ToolDefinition> GetTools();

    /// <summary>执行 model 产生的 Tool 调用，并返回结果文本。</summary>
    Task<string> InvokeAsync(ToolCall call, CancellationToken cancellationToken);
}
