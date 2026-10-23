using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Mcp;

/// <summary>
/// 将 MCP Tool 转换为 <see cref="ToolDefinition"/> 实例，并创建适用于聊天 Tool 循环的处理程序。
/// </summary>
public static class McpToolBridge
{
    /// <summary>
    /// 将 MCP Tool 转换为聊天 Tool 定义，并把名称净化为大多数聊天 API 允许的字符
    /// （<c>[a-zA-Z0-9_-]</c>）。发生冲突时追加 <c>_2</c>、<c>_3</c>、…… 后缀。
    /// </summary>
    /// <param name="tools">MCP 服务器报告的 Tool。</param>
    /// <returns>每个输入 Tool 对应一个 <see cref="ToolDefinition"/>。</returns>
    public static IReadOnlyList<ToolDefinition> ToToolDefinitions(IEnumerable<McpTool> tools)
    {
        if (tools == null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        var definitions = new List<ToolDefinition>();
        foreach (var entry in BuildEntries(tools))
        {
            definitions.Add(ToolDefinition.Create(
                entry.SanitizedName,
                entry.Tool.Description,
                entry.Tool.InputSchema));
        }

        return definitions;
    }

    /// <summary>
    /// 创建处理程序：将（可能已净化的）聊天 Tool 名称解析回原始 MCP Tool，
    /// 使用解析后的 <see cref="ToolCall.ArgumentsJson"/> 调用它，并返回文本结果。
    /// </summary>
    /// <param name="client">用于调用 Tool 的已初始化 MCP 客户端。</param>
    /// <param name="tools">传给 <see cref="ToToolDefinitions"/> 的同一组 Tool。</param>
    /// <returns>与聊天客户端 Tool 循环兼容的委托。</returns>
    public static Func<ToolCall, CancellationToken, Task<string>> CreateToolHandler(McpClient client, IEnumerable<McpTool> tools)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (tools == null)
        {
            throw new ArgumentNullException(nameof(tools));
        }

        var entries = BuildEntries(tools);
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            lookup[entry.Tool.Name] = entry.Tool.Name;
        }

        foreach (var entry in entries)
        {
            // 当服务器使用的名称与另一个 Tool 的净化形式相同时，净化后的名称优先于原始名称，
            // 因为 ToToolDefinitions 公布的就是净化后的名称。
            lookup[entry.SanitizedName] = entry.Tool.Name;
        }

        return async (call, ct) =>
        {
            if (call == null)
            {
                throw new ArgumentNullException(nameof(call));
            }

            if (!lookup.TryGetValue(call.Name, out var originalName))
            {
                throw new McpException(
                    $"The MCP tool '{call.Name}' is not available.",
                    JsonRpc.InvalidParams);
            }

            JsonElement? arguments = null;
            var json = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        arguments = document.RootElement.Clone();
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new McpException(
                    $"The arguments for MCP tool '{call.Name}' are not valid JSON.",
                    JsonRpc.InvalidParams,
                    ex.Message,
                    ex);
            }

            var result = await client.CallToolAsync(originalName, arguments, ct).ConfigureAwait(false);
            var text = result.GetText();
            if (text.Length == 0 && result.StructuredContent.HasValue)
            {
                text = result.StructuredContent.Value.GetRawText();
            }

            if (text.Length == 0 && result.IsError)
            {
                text = "The MCP tool reported an error without a message.";
            }

            return text;
        };
    }

    /// <summary>为仅接受 <c>[a-zA-Z0-9_-]</c> 的聊天 API 净化 MCP Tool 名称。</summary>
    /// <param name="name">原始 MCP Tool 名称。</param>
    /// <returns>非空的净化后名称。</returns>
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "tool";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            var valid = (character >= 'a' && character <= 'z')
                || (character >= 'A' && character <= 'Z')
                || (character >= '0' && character <= '9')
                || character == '_'
                || character == '-';
            builder.Append(valid ? character : '_');
        }

        return builder.Length == 0 ? "tool" : builder.ToString();
    }

    /// <summary>
    /// 按声明顺序构建 Tool 条目，并分配唯一的净化后名称。已使用（或在净化后发生冲突）的名称
    /// 会获得第一个可用的 <c>_2</c>、<c>_3</c>、…… 后缀。
    /// </summary>
    private static List<ToolEntry> BuildEntries(IEnumerable<McpTool> tools)
    {
        var entries = new List<ToolEntry>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var nextSuffix = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            if (tool == null)
            {
                continue;
            }

            var baseName = SanitizeName(tool.Name);
            var candidate = baseName;
            if (used.Contains(candidate))
            {
                int suffix;
                nextSuffix.TryGetValue(baseName, out suffix);
                if (suffix < 1)
                {
                    suffix = 1;
                }

                do
                {
                    suffix++;
                    candidate = baseName + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                while (used.Contains(candidate));

                nextSuffix[baseName] = suffix;
            }

            used.Add(candidate);
            entries.Add(new ToolEntry(tool, candidate));
        }

        return entries;
    }

    private sealed class ToolEntry
    {
        internal ToolEntry(McpTool tool, string sanitizedName)
        {
            Tool = tool;
            SanitizedName = sanitizedName;
        }

        internal McpTool Tool { get; }

        internal string SanitizedName { get; }
    }
}
