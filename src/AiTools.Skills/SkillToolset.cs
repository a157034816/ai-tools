using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Core.Tools;

namespace AiTools.Skills;

/// <summary>控制 skill 如何暴露给模型的选项。</summary>
public sealed class SkillToolOptions
{
    /// <summary>获取或设置 <c>read_skill_file</c> 返回的最大字符数。默认值为 200,000。</summary>
    public int MaxFileCharacters { get; set; } = 200_000;

    /// <summary>获取或设置 <c>read_skill_file</c> 是否可以读取 skill 目录之外的文件。默认值为 false。</summary>
    public bool AllowFilesOutsideSkillDirectory { get; set; }

    /// <summary>获取或设置追加到系统提示词中、说明如何使用 skill 的指令。</summary>
    public string? InstructionsOverride { get; set; }

    /// <summary>获取或设置绝不允许加载的 skill 名称。</summary>
    public IReadOnlyCollection<string>? DisabledSkills { get; set; }
}

/// <summary>
/// 使用渐进式披露将 <see cref="SkillCatalog"/> 转换为模型可调用的工具：
/// 系统提示词中只放置每个 skill 的名称和描述；当 skill 与任务相关时，模型调用
/// <c>load_skill</c>，并调用 <c>read_skill_file</c> 读取随附资源。
/// 可与任何 <see cref="IChatClient"/> 提供程序配合使用。
/// </summary>
public sealed class SkillToolset
{
    /// <summary>加载 skill 完整指令的工具名称。</summary>
    public const string LoadSkillToolName = "load_skill";

    /// <summary>读取随附 skill 文件的工具名称。</summary>
    public const string ReadSkillFileToolName = "read_skill_file";

    private readonly Dictionary<string, SkillDefinition> _skills;
    private readonly SkillToolOptions _options;

    /// <summary>从目录创建 toolset。</summary>
    public SkillToolset(SkillCatalog catalog, SkillToolOptions? options = null)
    {
        if (catalog == null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        _options = options ?? new SkillToolOptions();
        _skills = catalog.Skills
            .Where(skill => !IsDisabled(skill.Name))
            .ToDictionary(skill => skill.Name, StringComparer.OrdinalIgnoreCase);
        Catalog = catalog;
    }

    /// <summary>获取底层目录。</summary>
    public SkillCatalog Catalog { get; }

    /// <summary>获取已启用的 skill。</summary>
    public IReadOnlyList<SkillDefinition> Skills => _skills.Values.OrderBy(skill => skill.Name, StringComparer.Ordinal).ToArray();

    /// <summary>获取是否至少有一个 skill 可用。</summary>
    public bool HasSkills => _skills.Count > 0;

    /// <summary>
    /// 构建随聊天请求一起发送的工具定义。没有可用的 skill 时返回空列表。
    /// </summary>
    public IReadOnlyList<ToolDefinition> GetToolDefinitions()
    {
        if (_skills.Count == 0)
        {
            return Array.Empty<ToolDefinition>();
        }

        var loadTool = ToolBuilder.Create(
            LoadSkillToolName,
            "Loads the full instructions for a skill. Call this before using a skill whenever the task matches its description.",
            JsonSchemaBuilder.Object()
                .Property("name", JsonSchemaBuilder.String("The skill name exactly as listed in the skill catalog."))
                .Required("name"));

        var readTool = ToolBuilder.Create(
            ReadSkillFileToolName,
            "Reads a file bundled with a loaded skill (for example references/style-guide.md or scripts/check.py). Use this only after loading the skill that owns the file.",
            JsonSchemaBuilder.Object()
                .Property("skill", JsonSchemaBuilder.String("The skill name that owns the file."))
                .Property("path", JsonSchemaBuilder.String("File path relative to the skill directory, using forward slashes."))
                .Required("skill", "path"));

        return new[] { loadTool, readTool };
    }

    /// <summary>
    /// 创建供 <c>CompleteWithToolsAsync</c>、<c>StreamWithToolsAsync</c>、
    /// <c>StreamWithToolsExactAsync</c> 或任何 <see cref="ChatRequest"/> 工具循环使用的工具处理程序。
    /// </summary>
    public Func<ToolCall, System.Threading.CancellationToken, System.Threading.Tasks.Task<string>> CreateToolHandler()
    {
        return (call, cancellationToken) =>
        {
            if (call == null)
            {
                throw new ArgumentNullException(nameof(call));
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(call.Name, LoadSkillToolName, StringComparison.OrdinalIgnoreCase))
            {
                return System.Threading.Tasks.Task.FromResult(LoadSkill(call.ArgumentsJson));
            }

            if (string.Equals(call.Name, ReadSkillFileToolName, StringComparison.OrdinalIgnoreCase))
            {
                return System.Threading.Tasks.Task.FromResult(ReadSkillFile(call.ArgumentsJson));
            }

            return System.Threading.Tasks.Task.FromResult(Error($"Unknown skill tool '{call.Name}'."));
        };
    }

    /// <summary>
    /// 将 skill 目录（仅名称和描述）及使用说明追加到请求的系统提示词中。
    /// </summary>
    public ChatRequest ApplyTo(ChatRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (_skills.Count == 0)
        {
            return request;
        }

        var systemPrompt = CombineSystemPrompts(request.SystemPrompt, BuildSystemPrompt());
        var tools = request.Tools.Count == 0
            ? GetToolDefinitions()
            : request.Tools.Concat(GetToolDefinitions()).ToArray();

        return new ChatRequest
        {
            Model = request.Model,
            SystemPrompt = systemPrompt,
            Messages = request.Messages,
            Tools = tools,
            ToolChoice = request.ToolChoice,
            Temperature = request.Temperature,
            TopP = request.TopP,
            TopK = request.TopK,
            MaxOutputTokens = request.MaxOutputTokens,
            StopSequences = request.StopSequences,
            ResponseFormat = request.ResponseFormat,
            AdditionalParameters = request.AdditionalParameters
        };
    }

    /// <summary>构建列出可用 skill 的系统提示词部分。</summary>
    public string BuildSystemPrompt()
    {
        if (_skills.Count == 0)
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(_options.InstructionsOverride))
        {
            return _options.InstructionsOverride!;
        }

        var builder = new StringBuilder();
        builder.AppendLine("## Skills");
        builder.AppendLine();
        builder.AppendLine("You have access to skills: packages of instructions and resources for specific tasks.");
        builder.AppendLine($"When a task matches a skill's description, call the `{LoadSkillToolName}` tool with the skill name to load its full instructions **before** doing the work.");
        builder.AppendLine($"After loading a skill, use `{ReadSkillFileToolName}` to read any bundled file it references.");
        builder.AppendLine("Do not guess the contents of a skill; always load it first.");
        builder.AppendLine();
        builder.AppendLine("Available skills:");

        foreach (var skill in Skills)
        {
            builder.Append("- `").Append(skill.Name).Append("`: ").AppendLine(skill.Description);
        }

        return builder.ToString().TrimEnd();
    }

    private string LoadSkill(string argumentsJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        if (!document.RootElement.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return Error("The 'name' argument is required.");
        }

        var name = nameElement.GetString();
        if (string.IsNullOrEmpty(name))
        {
            return Error("The 'name' argument is required.");
        }

        if (!_skills.TryGetValue(name!, out var skill))
        {
            if (IsDisabled(name!))
            {
                return Error($"The skill '{name}' is disabled.");
            }

            var available = string.Join(", ", _skills.Keys.OrderBy(key => key, StringComparer.Ordinal));
            return Error($"Unknown skill '{name}'. Available skills: {available}.");
        }

        var builder = new StringBuilder();
        builder.Append("# Skill: ").Append(skill.Name).AppendLine();
        builder.AppendLine();
        builder.AppendLine("Skill directory: " + skill.DirectoryPath);
        builder.AppendLine();
        builder.AppendLine(skill.Instructions);
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine($"Bundled files can be read with the `{ReadSkillFileToolName}` tool using `skill=\"{skill.Name}\"` and a path relative to the skill directory.");

        return builder.ToString();
    }

    private string ReadSkillFile(string argumentsJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        if (!document.RootElement.TryGetProperty("skill", out var skillElement) || skillElement.ValueKind != JsonValueKind.String)
        {
            return Error("The 'skill' argument is required.");
        }

        if (!document.RootElement.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
        {
            return Error("The 'path' argument is required.");
        }

        var skillName = skillElement.GetString();
        var relativePath = pathElement.GetString();
        if (string.IsNullOrEmpty(skillName) || string.IsNullOrEmpty(relativePath))
        {
            return Error("Both 'skill' and 'path' are required.");
        }

        if (!_skills.TryGetValue(skillName!, out var skill))
        {
            return Error($"Unknown skill '{skillName}'. Load the skill first with `{LoadSkillToolName}`.");
        }

        var normalized = relativePath!.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            return Error("Paths containing '..' are not allowed.");
        }

        var root = Path.GetFullPath(skill.DirectoryPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!_options.AllowFilesOutsideSkillDirectory && !IsInside(fullPath, root))
        {
            return Error("The requested path is outside the skill directory.");
        }

        if (!File.Exists(fullPath))
        {
            return Error($"File '{normalized}' was not found in skill '{skill.Name}'.");
        }

        try
        {
            var text = File.ReadAllText(fullPath, Encoding.UTF8);
            if (text.Length > _options.MaxFileCharacters)
            {
                return text.Substring(0, _options.MaxFileCharacters) +
                       $"\n\n[truncated: file is {text.Length.ToString(CultureInfo.InvariantCulture)} characters; limit is {_options.MaxFileCharacters.ToString(CultureInfo.InvariantCulture)}]";
            }

            return text;
        }
        catch (Exception ex)
        {
            return Error($"Failed to read '{normalized}': {ex.Message}");
        }
    }

    private bool IsDisabled(string name)
        => _options.DisabledSkills != null &&
           _options.DisabledSkills.Any(disabled => string.Equals(disabled, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsInside(string fullPath, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineSystemPrompts(string? existing, string addition)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return addition;
        }

        return existing!.TrimEnd() + "\n\n" + addition;
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, JsonSerializerOptions);
    private static readonly JsonSerializerOptions JsonSerializerOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>用于将 skill 应用到聊天请求的便捷扩展方法。</summary>
public static class SkillChatExtensions
{
    /// <summary>将 skill 目录和工具应用到请求。</summary>
    public static ChatRequest WithSkills(this ChatRequest request, SkillToolset skills)
    {
        if (skills == null)
        {
            throw new ArgumentNullException(nameof(skills));
        }

        return skills.ApplyTo(request);
    }

    /// <summary>
    /// 在启用 skill 的情况下运行聊天请求，使用标准工具循环。
    /// </summary>
    /// <param name="client">聊天客户端。</param>
    /// <param name="request">要发送的请求。</param>
    /// <param name="skills">skill toolset。</param>
    /// <param name="additionalToolHandler">用于处理 skill 工具之外其他工具的可选处理程序。</param>
    /// <param name="maxToolRounds">最大工具轮数。</param>
    /// <param name="cancellationToken">Cancellation token。</param>
    public static System.Threading.Tasks.Task<ChatResponse> CompleteWithSkillsAsync(
        this IChatClient client,
        ChatRequest request,
        SkillToolset skills,
        Func<ToolCall, System.Threading.CancellationToken, System.Threading.Tasks.Task<string>>? additionalToolHandler = null,
        int maxToolRounds = 8,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (skills == null)
        {
            throw new ArgumentNullException(nameof(skills));
        }

        var skillHandler = skills.CreateToolHandler();
        var prepared = skills.ApplyTo(request);

        return client.CompleteWithToolsAsync(
            prepared,
            async (call, ct) =>
            {
                if (string.Equals(call.Name, SkillToolset.LoadSkillToolName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(call.Name, SkillToolset.ReadSkillFileToolName, StringComparison.OrdinalIgnoreCase))
                {
                    return await skillHandler(call, ct).ConfigureAwait(false);
                }

                if (additionalToolHandler != null)
                {
                    return await additionalToolHandler(call, ct).ConfigureAwait(false);
                }

                return JsonSerializer.Serialize(new { error = $"Unknown tool '{call.Name}'." });
            },
            maxToolRounds,
            cancellationToken);
    }
}
