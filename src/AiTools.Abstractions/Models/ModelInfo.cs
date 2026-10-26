using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Abstractions;

/// <summary>服务商模型列表中的一项。</summary>
public sealed class ModelInfo
{
    /// <summary>发送给服务商 API 的模型标识符，例如 <c>gpt-4.1</c>。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>服务商提供的可读名称。为空时回退到 <see cref="Id"/>。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>提供方（仅部分服务商返回）。</summary>
    public string? OwnedBy { get; init; }

    /// <summary>模型创建时间（仅部分服务商返回）。</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>获取用于在 UI 中显示的名称。</summary>
    public string GetDisplayName() => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}

/// <summary>从服务商接口获取当前可用的模型列表。</summary>
public interface IModelCatalog
{
    /// <summary>服务商标识，例如 "openai"、"anthropic"。</summary>
    string ProviderName { get; }

    /// <summary>获取服务商当前支持的模型列表。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);
}
