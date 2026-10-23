using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace AiTools.Mcp;

/// <summary>描述由 MCP 服务器暴露的 Tool。</summary>
public sealed class McpTool
{
    /// <summary>服务器报告的 Tool 名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>可选的人类可读描述，供模型选择 Tool 时参考。</summary>
    public string? Description { get; init; }

    /// <summary>描述 Tool 参数的 JSON Schema（服务器提供时）。</summary>
    public JsonElement? InputSchema { get; init; }

    /// <summary>服务器报告的原始 annotation 对象。</summary>
    public JsonElement? Annotations { get; init; }

    /// <summary><see cref="Annotations"/> 的解析形式（如存在）。</summary>
    public McpToolAnnotations? ParsedAnnotations { get; init; }
}

/// <summary>MCP Tool 的行为提示。</summary>
public sealed class McpToolAnnotations
{
    /// <summary>可选的显示标题。</summary>
    public string? Title { get; init; }

    /// <summary>指示 Tool 是否不会修改其所在环境。</summary>
    public bool? ReadOnlyHint { get; init; }

    /// <summary>指示 Tool 是否可能执行破坏性更新。</summary>
    public bool? DestructiveHint { get; init; }

    /// <summary>指示使用相同参数重复调用是否不会产生额外影响。</summary>
    public bool? IdempotentHint { get; init; }

    /// <summary>指示 Tool 是否可能与外部实体交互。</summary>
    public bool? OpenWorldHint { get; init; }
}

/// <summary><c>tools/call</c> 请求的结果。</summary>
public sealed class McpCallToolResult
{
    /// <summary>Tool 返回的内容块。</summary>
    public IReadOnlyList<McpContent> Content { get; init; } = Array.Empty<McpContent>();

    /// <summary>Tool 是否报告了执行错误。</summary>
    public bool IsError { get; init; }

    /// <summary>由 Tool 输出 schema 定义的可选结构化结果对象。</summary>
    public JsonElement? StructuredContent { get; init; }

    /// <summary>服务器返回的原始 JSON 结果。</summary>
    public JsonElement? Raw { get; init; }

    /// <summary>
    /// 拼接 Tool 返回的文本内容，包括嵌入在 Resource 内容块中的文本。
    /// 多个块之间以换行符分隔。
    /// </summary>
    /// <returns>拼接后的文本；结果不含文本内容时为空字符串。</returns>
    public string GetText()
    {
        var builder = new StringBuilder();
        foreach (var content in Content)
        {
            string? text = content switch
            {
                McpTextContent textContent => textContent.Text,
                McpResourceContent resourceContent => resourceContent.Text,
                McpEmbeddedResourceContent embedded => embedded.Resource.Text,
                _ => null
            };

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }
}

/// <summary>与 MCP 服务器交换的内容块的基类型。</summary>
public abstract class McpContent
{
    /// <summary>初始化内容块。</summary>
    protected McpContent()
    {
    }
}

/// <summary>纯文本内容。</summary>
public sealed class McpTextContent : McpContent
{
    /// <summary>文本值。</summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>Base64 编码的图像内容。</summary>
public sealed class McpImageContent : McpContent
{
    /// <summary>Base64 编码的图像字节。</summary>
    public string Data { get; init; } = string.Empty;

    /// <summary>图像 MIME 类型，例如 <c>image/png</c>。</summary>
    public string MimeType { get; init; } = string.Empty;
}

/// <summary>Base64 编码的音频内容。</summary>
public sealed class McpAudioContent : McpContent
{
    /// <summary>Base64 编码的音频字节。</summary>
    public string Data { get; init; } = string.Empty;

    /// <summary>音频 MIME 类型，例如 <c>audio/wav</c>。</summary>
    public string MimeType { get; init; } = string.Empty;
}

/// <summary>指向服务器暴露的 Resource 的引用（例如 <c>resource_link</c> 内容块）。</summary>
public sealed class McpResourceContent : McpContent
{
    /// <summary>Resource 的 URI。</summary>
    public string Uri { get; init; } = string.Empty;

    /// <summary>可选的 Resource MIME 类型。</summary>
    public string? MimeType { get; init; }

    /// <summary>可选的内联文本。</summary>
    public string? Text { get; init; }

    /// <summary>可选的 Base64 编码二进制数据。</summary>
    public string? Blob { get; init; }
}

/// <summary>在响应中直接嵌入 Resource 内容的内容块。</summary>
public sealed class McpEmbeddedResourceContent : McpContent
{
    /// <summary>嵌入的 Resource 内容。</summary>
    public McpResourceContents Resource { get; init; } = new McpResourceContents();
}

/// <summary>单个 MCP Resource 的内容。</summary>
public sealed class McpResourceContents
{
    /// <summary>Resource 的 URI。</summary>
    public string Uri { get; init; } = string.Empty;

    /// <summary>可选的 Resource MIME 类型。</summary>
    public string? MimeType { get; init; }

    /// <summary>UTF-8 文本内容（Resource 为文本类型时）。</summary>
    public string? Text { get; init; }

    /// <summary>Base64 编码的二进制内容（Resource 为二进制类型时）。</summary>
    public string? Blob { get; init; }
}

/// <summary>由 MCP 服务器暴露的具体 Resource。</summary>
public sealed class McpResource
{
    /// <summary>Resource 的 URI。</summary>
    public string Uri { get; init; } = string.Empty;

    /// <summary>人类可读的 Resource 名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>可选的描述。</summary>
    public string? Description { get; init; }

    /// <summary>可选的 MIME 类型。</summary>
    public string? MimeType { get; init; }

    /// <summary>服务器报告的原始 annotation 对象。</summary>
    public JsonElement? Annotations { get; init; }

    /// <summary>可选的大小（字节）。</summary>
    public int? Size { get; init; }
}

/// <summary>可生成具体 Resource 的 URI 模板。</summary>
public sealed class McpResourceTemplate
{
    /// <summary>RFC 6570 URI 模板。</summary>
    public string UriTemplate { get; init; } = string.Empty;

    /// <summary>人类可读的模板名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>可选的描述。</summary>
    public string? Description { get; init; }

    /// <summary>由模板生成的 Resource 的可选 MIME 类型。</summary>
    public string? MimeType { get; init; }
}

/// <summary><c>resources/read</c> 请求的结果。</summary>
public sealed class McpReadResourceResult
{
    /// <summary>服务器返回的 Resource 内容。</summary>
    public IReadOnlyList<McpResourceContents> Contents { get; init; } = Array.Empty<McpResourceContents>();
}

/// <summary>由 MCP 服务器暴露的 Prompt。</summary>
public sealed class McpPrompt
{
    /// <summary>Prompt 名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>可选的描述。</summary>
    public string? Description { get; init; }

    /// <summary>可选的 Prompt 参数。</summary>
    public IReadOnlyList<McpPromptArgument>? Arguments { get; init; }
}

/// <summary>MCP Prompt 接受的参数。</summary>
public sealed class McpPromptArgument
{
    /// <summary>参数名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>可选的描述。</summary>
    public string? Description { get; init; }

    /// <summary>指示该参数是否必须提供。</summary>
    public bool Required { get; init; }
}

/// <summary><c>prompts/get</c> 请求的结果。</summary>
public sealed class McpGetPromptResult
{
    /// <summary>对渲染后 Prompt 的可选描述。</summary>
    public string? Description { get; init; }

    /// <summary>要发送给模型的 Prompt 消息。</summary>
    public IReadOnlyList<McpPromptMessage> Messages { get; init; } = Array.Empty<McpPromptMessage>();
}

/// <summary>由 <c>prompts/get</c> 返回的单条消息。</summary>
public sealed class McpPromptMessage
{
    /// <summary>消息角色，例如 <c>user</c> 或 <c>assistant</c>。</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>消息内容。</summary>
    public McpContent Content { get; init; } = new McpTextContent();
}

/// <summary>MCP 服务器为长时间运行的请求报告的 progress 信息。</summary>
public sealed class McpProgress
{
    /// <summary>随发起请求一同提供的 progress Token，已规范化为字符串。</summary>
    public string? ProgressToken { get; init; }

    /// <summary>当前 progress 值。</summary>
    public double Progress { get; init; }

    /// <summary>可选的总量值，可用于将 progress 显示为百分比。</summary>
    public double? Total { get; init; }

    /// <summary>可选的人类可读状态消息。</summary>
    public string? Message { get; init; }
}

/// <summary>本库无法识别的内容块类型。原始 JSON 会被保留。</summary>
public sealed class McpUnknownContent : McpContent
{
    /// <summary>服务器报告的 <c>type</c> 值（如存在）。</summary>
    public string? Type { get; init; }

    /// <summary>内容块的原始 JSON。</summary>
    public JsonElement Raw { get; init; }
}
