using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using AiTools.Core.Json;

namespace AiTools.Mcp;

/// <summary>将 MCP JSON 载荷映射到公共模型类型的防御性解析器。</summary>
internal static class McpJsonParser
{
    internal static IReadOnlyList<McpTool> ParseTools(JsonElement result)
    {
        var tools = new List<McpTool>();
        var array = JsonHelpers.GetArray(result, "tools");
        if (array == null)
        {
            return tools;
        }

        foreach (var item in array.Value)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var annotations = JsonHelpers.GetObject(item, "annotations");
            tools.Add(new McpTool
            {
                Name = JsonHelpers.GetString(item, "name") ?? string.Empty,
                Description = JsonHelpers.GetString(item, "description"),
                InputSchema = CloneObject(item, "inputSchema"),
                Annotations = annotations,
                ParsedAnnotations = ParseAnnotations(annotations)
            });
        }

        return tools;
    }

    internal static McpToolAnnotations? ParseAnnotations(JsonElement? annotations)
    {
        if (!JsonValue.IsUsable(annotations) || annotations!.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var element = annotations.Value;
        return new McpToolAnnotations
        {
            Title = JsonHelpers.GetString(element, "title"),
            ReadOnlyHint = JsonHelpers.GetBool(element, "readOnlyHint"),
            DestructiveHint = JsonHelpers.GetBool(element, "destructiveHint"),
            IdempotentHint = JsonHelpers.GetBool(element, "idempotentHint"),
            OpenWorldHint = JsonHelpers.GetBool(element, "openWorldHint")
        };
    }

    internal static McpCallToolResult ParseToolResult(JsonElement result)
    {
        var content = new List<McpContent>();
        var array = JsonHelpers.GetArray(result, "content");
        if (array != null)
        {
            foreach (var item in array.Value)
            {
                content.Add(ParseContent(item));
            }
        }

        return new McpCallToolResult
        {
            Content = content,
            IsError = JsonHelpers.GetBool(result, "isError") ?? false,
            StructuredContent = CloneObject(result, "structuredContent"),
            Raw = JsonValue.IsUsable(result) ? result.Clone() : (JsonElement?)null
        };
    }

    internal static McpContent ParseContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return new McpTextContent { Text = element.GetString() ?? string.Empty };
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return new McpUnknownContent { Raw = element.Clone() };
        }

        var type = JsonHelpers.GetString(element, "type");
        switch (type)
        {
            case "text":
                return new McpTextContent { Text = JsonHelpers.GetString(element, "text") ?? string.Empty };

            case "image":
                return new McpImageContent
                {
                    Data = JsonHelpers.GetString(element, "data") ?? string.Empty,
                    MimeType = JsonHelpers.GetString(element, "mimeType") ?? string.Empty
                };

            case "audio":
                return new McpAudioContent
                {
                    Data = JsonHelpers.GetString(element, "data") ?? string.Empty,
                    MimeType = JsonHelpers.GetString(element, "mimeType") ?? string.Empty
                };

            case "resource":
                return ParseEmbeddedResource(element);

            case "resource_link":
                return new McpResourceContent
                {
                    Uri = JsonHelpers.GetString(element, "uri") ?? string.Empty,
                    MimeType = JsonHelpers.GetString(element, "mimeType")
                };

            default:
                return new McpUnknownContent { Type = type, Raw = element.Clone() };
        }
    }

    internal static IReadOnlyList<McpResource> ParseResources(JsonElement result)
    {
        var resources = new List<McpResource>();
        var array = JsonHelpers.GetArray(result, "resources");
        if (array == null)
        {
            return resources;
        }

        foreach (var item in array.Value)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            resources.Add(new McpResource
            {
                Uri = JsonHelpers.GetString(item, "uri") ?? string.Empty,
                Name = JsonHelpers.GetString(item, "name") ?? string.Empty,
                Description = JsonHelpers.GetString(item, "description"),
                MimeType = JsonHelpers.GetString(item, "mimeType"),
                Annotations = JsonHelpers.GetObject(item, "annotations"),
                Size = JsonHelpers.GetInt32(item, "size")
            });
        }

        return resources;
    }

    internal static McpReadResourceResult ParseReadResourceResult(JsonElement result)
    {
        var contents = new List<McpResourceContents>();
        var array = JsonHelpers.GetArray(result, "contents");
        if (array != null)
        {
            foreach (var item in array.Value)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                contents.Add(ParseResourceContents(item));
            }
        }

        return new McpReadResourceResult { Contents = contents };
    }

    internal static McpResourceContents ParseResourceContents(JsonElement element)
    {
        return new McpResourceContents
        {
            Uri = JsonHelpers.GetString(element, "uri") ?? string.Empty,
            MimeType = JsonHelpers.GetString(element, "mimeType"),
            Text = JsonHelpers.GetString(element, "text"),
            Blob = JsonHelpers.GetString(element, "blob")
        };
    }

    internal static IReadOnlyList<McpResourceTemplate> ParseResourceTemplates(JsonElement result)
    {
        var templates = new List<McpResourceTemplate>();
        var array = JsonHelpers.GetArray(result, "resourceTemplates");
        if (array == null)
        {
            return templates;
        }

        foreach (var item in array.Value)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            templates.Add(new McpResourceTemplate
            {
                UriTemplate = JsonHelpers.GetString(item, "uriTemplate") ?? string.Empty,
                Name = JsonHelpers.GetString(item, "name") ?? string.Empty,
                Description = JsonHelpers.GetString(item, "description"),
                MimeType = JsonHelpers.GetString(item, "mimeType")
            });
        }

        return templates;
    }

    internal static IReadOnlyList<McpPrompt> ParsePrompts(JsonElement result)
    {
        var prompts = new List<McpPrompt>();
        var array = JsonHelpers.GetArray(result, "prompts");
        if (array == null)
        {
            return prompts;
        }

        foreach (var item in array.Value)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            List<McpPromptArgument>? arguments = null;
            var argumentArray = JsonHelpers.GetArray(item, "arguments");
            if (argumentArray != null)
            {
                arguments = new List<McpPromptArgument>();
                foreach (var argument in argumentArray.Value)
                {
                    if (argument.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    arguments.Add(new McpPromptArgument
                    {
                        Name = JsonHelpers.GetString(argument, "name") ?? string.Empty,
                        Description = JsonHelpers.GetString(argument, "description"),
                        Required = JsonHelpers.GetBool(argument, "required") ?? false
                    });
                }
            }

            prompts.Add(new McpPrompt
            {
                Name = JsonHelpers.GetString(item, "name") ?? string.Empty,
                Description = JsonHelpers.GetString(item, "description"),
                Arguments = arguments
            });
        }

        return prompts;
    }

    internal static McpGetPromptResult ParseGetPromptResult(JsonElement result)
    {
        var messages = new List<McpPromptMessage>();
        var array = JsonHelpers.GetArray(result, "messages");
        if (array != null)
        {
            foreach (var item in array.Value)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                McpContent content;
                if (item.TryGetProperty("content", out var contentElement))
                {
                    content = ParseContent(contentElement);
                }
                else
                {
                    content = new McpTextContent();
                }

                messages.Add(new McpPromptMessage
                {
                    Role = JsonHelpers.GetString(item, "role") ?? string.Empty,
                    Content = content
                });
            }
        }

        return new McpGetPromptResult
        {
            Description = JsonHelpers.GetString(result, "description"),
            Messages = messages
        };
    }

    internal static McpProgress? ParseProgress(JsonElement notification)
    {
        if (notification.ValueKind != JsonValueKind.Object
            || !notification.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var progress = GetDouble(parameters, "progress");
        if (progress == null)
        {
            return null;
        }

        return new McpProgress
        {
            ProgressToken = parameters.TryGetProperty("progressToken", out var token)
                ? JsonHelpers.GetStringOrNull(token)
                : null,
            Progress = progress.Value,
            Total = GetDouble(parameters, "total"),
            Message = JsonHelpers.GetString(parameters, "message")
        };
    }

    internal static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static McpContent ParseEmbeddedResource(JsonElement element)
    {
        if (element.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object)
        {
            return new McpEmbeddedResourceContent { Resource = ParseResourceContents(resource) };
        }

        // 容忍将资源字段直接内联的服务器。
        return new McpEmbeddedResourceContent { Resource = ParseResourceContents(element) };
    }

    private static JsonElement? CloneObject(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value)
            && value.ValueKind != JsonValueKind.Undefined
            && value.ValueKind != JsonValueKind.Null)
        {
            return value.Clone();
        }

        return null;
    }
}
