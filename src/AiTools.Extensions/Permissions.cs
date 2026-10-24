using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AiTools.Extensions;

/// <summary>项目资源的信任级别。</summary>
public enum TrustLevel
{
    /// <summary>项目目录位于受信任的根下，其贡献可以加载。</summary>
    Trusted,

    /// <summary>项目目录不受信任；项目级贡献应被跳过或降级。</summary>
    Untrusted,

    /// <summary>调用方显式拒绝加载。</summary>
    Blocked
}

/// <summary>单条工具调用规则给出的结论。</summary>
public enum PermissionDecision
{
    /// <summary>直接执行。</summary>
    Allow,

    /// <summary>需要用户确认；没有确认通道时按拒绝处理。</summary>
    Ask,

    /// <summary>不执行。</summary>
    Deny
}

/// <summary>
/// 一条工具调用规则。
/// </summary>
public sealed class PermissionRule
{
    /// <summary>
    /// 获取或设置工具名匹配模式，默认 <c>*</c>。支持精确名、<c>*</c>、前缀形式
    /// <c>ns:*</c>（匹配 <c>ns:</c> 开头的任意工具名）；匹配区分大小写。
    /// </summary>
    public string Tool { get; init; } = "*";

    /// <summary>获取或设置结论。</summary>
    public PermissionDecision Decision { get; init; } = PermissionDecision.Ask;

    /// <summary>获取或设置规则来源，例如 <c>project</c>、<c>user</c>、<c>extension</c>。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// 获取或设置对参数 JSON 的 glob 约束（仅支持 <c>*</c> 通配）。
    /// 为 null 或空白时不约束参数。匹配区分大小写、作用于整段 JSON 文本。
    /// </summary>
    public string? ArgumentPattern { get; init; }

    /// <inheritdoc />
    public override string ToString() => Source + ":" + Tool + "=" + Decision;
}

/// <summary>
/// 有序的工具调用策略。结论优先级为 <c>deny &gt; ask &gt; allow</c>，
/// 同一优先级内先出现者获胜；没有任何规则命中时返回 <see cref="PermissionDecision.Ask"/>（fail closed）。
/// </summary>
public sealed class PermissionPolicy
{
    private readonly PermissionRule[] _rules;

    /// <summary>创建策略。</summary>
    /// <param name="rules">规则，按声明顺序求值；null 视为空。</param>
    public PermissionPolicy(IEnumerable<PermissionRule>? rules)
    {
        _rules = rules == null
            ? Array.Empty<PermissionRule>()
            : rules.Where(rule => rule != null).ToArray();
    }

    /// <summary>获取按声明顺序排列的规则。</summary>
    public IReadOnlyList<PermissionRule> Rules => _rules;

    /// <summary>获取空策略：所有工具都需要确认。</summary>
    public static PermissionPolicy Default { get; } = new(Array.Empty<PermissionRule>());

    /// <summary>从规则集创建策略。<paramref name="rules"/> 为 null 时返回 <see cref="Default"/>。</summary>
    public static PermissionPolicy FromRules(IEnumerable<PermissionRule>? rules)
        => rules == null ? Default : new PermissionPolicy(rules);

    /// <summary>求值工具调用。</summary>
    /// <param name="toolName">工具名；为 null 或空白时视为未知工具。</param>
    /// <param name="argumentsJson">参数 JSON；null 视为空对象。</param>
    /// <returns>结论；未知工具或没有规则命中时为 <see cref="PermissionDecision.Ask"/>。</returns>
    public PermissionDecision Evaluate(string? toolName, string argumentsJson)
        => Evaluate(toolName, argumentsJson, out _);

    /// <summary>求值工具调用并带出命中的规则。</summary>
    /// <param name="toolName">工具名；为 null 或空白时视为未知工具。</param>
    /// <param name="argumentsJson">参数 JSON；null 视为空对象。</param>
    /// <param name="matched">命中的规则；没有命中时为 null。</param>
    public PermissionDecision Evaluate(string? toolName, string argumentsJson, out PermissionRule? matched)
    {
        matched = null;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return PermissionDecision.Ask;
        }

        var name = toolName!.Trim();
        var arguments = argumentsJson ?? "{}";
        foreach (var rule in _rules)
        {
            if (!MatchesTool(rule.Tool, name) || !MatchesArguments(rule.ArgumentPattern, arguments))
            {
                continue;
            }

            // 只被更高优先级的结论替换，因此同一优先级内先出现者获胜。
            if (matched == null || Rank(rule.Decision) < Rank(matched.Decision))
            {
                matched = rule;
            }

            if (rule.Decision == PermissionDecision.Deny)
            {
                break;
            }
        }

        return matched?.Decision ?? PermissionDecision.Ask;
    }

    private static int Rank(PermissionDecision decision)
        => decision switch
        {
            PermissionDecision.Deny => 0,
            PermissionDecision.Ask => 1,
            _ => 2
        };

    private static bool MatchesTool(string? pattern, string toolName)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        var value = pattern!.Trim();
        if (value.EndsWith(":*", StringComparison.Ordinal))
        {
            return toolName.StartsWith(value.Substring(0, value.Length - 1), StringComparison.Ordinal);
        }

        return string.Equals(value, toolName, StringComparison.Ordinal);
    }

    private static bool MatchesArguments(string? pattern, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        return GlobMatch(pattern!.Trim(), argumentsJson);
    }

    /// <summary>只支持 <c>*</c> 的 glob 匹配；区分大小写。</summary>
    private static bool GlobMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && pattern[p] == text[t])
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}

/// <summary>项目信任决策的结果。</summary>
public sealed class TrustDecision
{
    /// <summary>创建决策。</summary>
    /// <param name="level">信任级别。</param>
    /// <param name="reason">人类可读的原因。</param>
    /// <param name="skippedSources">因该决策而跳过的项目来源（通常是规范化的项目目录）。</param>
    public TrustDecision(TrustLevel level, string? reason, IReadOnlyList<string>? skippedSources = null)
    {
        Level = level;
        Reason = reason;
        SkippedSources = skippedSources ?? Array.Empty<string>();
    }

    /// <summary>获取信任级别。</summary>
    public TrustLevel Level { get; }

    /// <summary>获取原因；无原因时为 null。</summary>
    public string? Reason { get; }

    /// <summary>获取被跳过的项目来源。</summary>
    public IReadOnlyList<string> SkippedSources { get; }

    /// <summary>获取项目贡献是否可以加载。</summary>
    public bool IsTrusted => Level == TrustLevel.Trusted;

    /// <inheritdoc />
    public override string ToString() => Level + (Reason == null ? string.Empty : " (" + Reason + ")");
}

/// <summary>
/// 加载项目资源前的信任判定。<see cref="Evaluate"/> 不读取环境变量、不缓存，
/// 受信任根由调用方显式传入。
/// </summary>
public static class ProjectTrust
{
    /// <summary>判断项目目录是否受信任。</summary>
    /// <param name="workingDirectory">项目目录；为 null 或无法规范化时不信任。</param>
    /// <param name="trustedRoots">受信任的根目录集合（区分大小写不敏感、分隔符归一化）。</param>
    /// <param name="requireTrust">为 false 时直接返回 <see cref="TrustLevel.Trusted"/>。</param>
    /// <returns>
    /// 位于某个受信任根下（含根本身）时为 <see cref="TrustLevel.Trusted"/>；
    /// 否则为 <see cref="TrustLevel.Untrusted"/>，并把规范化的项目目录记入 <see cref="TrustDecision.SkippedSources"/>。
    /// </returns>
    public static TrustDecision Evaluate(
        string? workingDirectory,
        IReadOnlyCollection<string>? trustedRoots,
        bool requireTrust)
    {
        if (!requireTrust)
        {
            return new TrustDecision(TrustLevel.Trusted, "Project trust is not required.");
        }

        var candidate = Normalize(workingDirectory);
        if (candidate == null)
        {
            return new TrustDecision(TrustLevel.Untrusted, "The project directory is missing or invalid.");
        }

        if (trustedRoots != null)
        {
            foreach (var declared in trustedRoots)
            {
                var root = Normalize(declared);
                if (root != null && IsUnder(candidate, root))
                {
                    return new TrustDecision(TrustLevel.Trusted, "The project directory is under the trusted root '" + root + "'.");
                }
            }
        }

        return new TrustDecision(
            TrustLevel.Untrusted,
            "The project directory is not under any trusted root.",
            new[] { candidate });
    }

    /// <summary>规范化路径：绝对化、统一分隔符、去掉尾部多余分隔符。</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = System.IO.Path.GetFullPath(path!.Trim());
            full = full.Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar);
            var trimmed = full.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (trimmed.Length == 0)
            {
                return full;
            }

            // 驱动器根（C:）必须保留分隔符，否则会被当成驱动器相对路径。
            return trimmed.EndsWith(":", StringComparison.Ordinal)
                ? trimmed + System.IO.Path.DirectorySeparatorChar
                : trimmed;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>判断路径是否等于根或位于根下；不使用字符串前缀比较，避免 <c>C:/a</c> 命中 <c>C:/ab</c>。</summary>
    private static bool IsUnder(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var separator = System.IO.Path.DirectorySeparatorChar.ToString();
        var prefix = root.EndsWith(separator, StringComparison.Ordinal)
            ? root
            : root + separator;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>工具调用被策略拒绝时抛出的异常。</summary>
public sealed class PermissionDeniedException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="toolName">被拒绝的工具名。</param>
    /// <param name="reason">拒绝原因。</param>
    public PermissionDeniedException(string toolName, string reason)
        : base("Tool '" + toolName + "' was denied: " + reason)
    {
        ToolName = toolName;
        Reason = reason;
    }

    /// <summary>获取被拒绝的工具名。</summary>
    public string ToolName { get; }

    /// <summary>获取拒绝原因。</summary>
    public string Reason { get; }
}

/// <summary>
/// 执行前的权限闸门。被拒绝时返回 JSON 错误文本而不执行；没有确认通道时按拒绝处理（fail closed）。
/// </summary>
public static class PermissionGate
{
    /// <summary>按策略守卫一次工具调用。</summary>
    /// <param name="policy">策略；为 null 时使用 <see cref="PermissionPolicy.Default"/>。</param>
    /// <param name="toolName">工具名。</param>
    /// <param name="argumentsJson">参数 JSON。</param>
    /// <param name="execute">实际执行体。</param>
    /// <param name="confirm">用户确认回调，参数为提示文本；为 null 时 <see cref="PermissionDecision.Ask"/> 一律拒绝。</param>
    /// <returns>工具结果，或 <c>{"error":"denied"}</c> / <c>{"error":"not approved"}</c> JSON。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="execute"/> 为 null。</exception>
    public static async Task<string> GuardAsync(
        PermissionPolicy policy,
        string toolName,
        string argumentsJson,
        Func<Task<string>> execute,
        Func<string, Task<bool>>? confirm)
    {
        if (execute == null)
        {
            throw new ArgumentNullException(nameof(execute));
        }

        policy ??= PermissionPolicy.Default;
        var decision = policy.Evaluate(toolName, argumentsJson, out var rule);
        switch (decision)
        {
            case PermissionDecision.Allow:
                return await execute().ConfigureAwait(false);

            case PermissionDecision.Deny:
                return ErrorJson("denied", Describe(toolName, rule, "is denied by"));

            default:
                if (confirm == null)
                {
                    return ErrorJson("not approved", Describe(toolName, rule, "requires approval from"));
                }

                bool approved;
                try
                {
                    approved = await confirm(BuildPrompt(toolName, argumentsJson)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    approved = false;
                }

                return approved
                    ? await execute().ConfigureAwait(false)
                    : ErrorJson("not approved", Describe(toolName, rule, "was not approved by"));
        }
    }

    private static string BuildPrompt(string toolName, string argumentsJson)
        => "Allow tool '" + toolName + "' with arguments " + (string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) + "?";

    private static string Describe(string toolName, PermissionRule? rule, string verb)
    {
        var source = rule == null || string.IsNullOrEmpty(rule.Source) ? "policy" : "'" + rule.Source + "' rule";
        return "Tool '" + toolName + "' " + verb + " " + source + ".";
    }

    private static string ErrorJson(string error, string reason)
        => "{\"error\":\"" + Escape(error) + "\",\"reason\":\"" + Escape(reason) + "\"}";

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// 一次会话的访问控制：信任决策与工具策略。宿主持有它，在每次工具调用前调用
/// <see cref="GuardAsync"/>，并在加载项目级插件前检查 <see cref="AllowsProjectResources"/>。
/// </summary>
public sealed class AccessControl
{
    /// <summary>创建访问控制。</summary>
    /// <param name="policy">工具策略。</param>
    /// <param name="trust">项目信任决策。</param>
    public AccessControl(PermissionPolicy policy, TrustDecision trust)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Trust = trust ?? throw new ArgumentNullException(nameof(trust));
    }

    /// <summary>获取全放行且不要求信任的实例（用于未启用权限模型的宿主）。</summary>
    public static AccessControl Unrestricted { get; } = new(PermissionPolicy.Default, new TrustDecision(TrustLevel.Trusted, "Access control is not enabled."));

    /// <summary>获取工具策略。</summary>
    public PermissionPolicy Policy { get; }

    /// <summary>获取项目信任决策。</summary>
    public TrustDecision Trust { get; }

    /// <summary>获取项目级资源（项目插件、项目设置）是否可以加载。</summary>
    public bool AllowsProjectResources => Trust.Level == TrustLevel.Trusted;

    /// <summary>按 <see cref="Policy"/> 守卫一次工具调用。</summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="argumentsJson">参数 JSON。</param>
    /// <param name="execute">实际执行体。</param>
    /// <param name="confirm">用户确认回调。</param>
    public Task<string> GuardAsync(string toolName, string argumentsJson, Func<Task<string>> execute, Func<string, Task<bool>>? confirm)
        => PermissionGate.GuardAsync(Policy, toolName, argumentsJson, execute, confirm);
}
