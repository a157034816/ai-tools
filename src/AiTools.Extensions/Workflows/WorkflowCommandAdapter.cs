using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions.Workflows;

/// <summary>把已加载的工作流包装成一个扩展：每个工作流一条斜杠命令，外加一个列出全部工作流的工具。</summary>
public static class WorkflowCommandAdapter
{
    /// <summary>列出工作流的工具名。</summary>
    public const string ListToolName = "workflow_list";

    /// <summary>创建扩展。</summary>
    /// <param name="workflows">工作流定义；同名者先到先得。</param>
    /// <param name="optionsFactory">每次运行前创建运行时选项（命令上下文与工作目录在此时已知）。</param>
    /// <param name="extensionName">扩展名，同时用作状态桶与命令归属。</param>
    public static IExtension CreateExtension(
        IReadOnlyList<WorkflowDefinition> workflows,
        Func<WorkflowRuntimeOptions> optionsFactory,
        string extensionName = "workflows")
    {
        if (workflows == null)
        {
            throw new ArgumentNullException(nameof(workflows));
        }

        if (optionsFactory == null)
        {
            throw new ArgumentNullException(nameof(optionsFactory));
        }

        return new WorkflowExtension(workflows, optionsFactory, string.IsNullOrWhiteSpace(extensionName) ? "workflows" : extensionName);
    }

    /// <summary>
    /// 把命令参数渲染成脚本里的 <c>arguments</c>：沿用 <see cref="CommandTemplateRenderer"/> 的
    /// <c>$ARGUMENTS</c>、<c>$ARGUMENTS[N]</c>、<c>$N</c>、<c>$name</c> 规则（含 <c>\$</c> 转义）。
    /// </summary>
    /// <param name="template">参数模板（<c>meta.arguments</c>）。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="parameters">工作流声明的具名参数。</param>
    public static string RenderArguments(string template, CommandArguments arguments, IReadOnlyList<string>? parameters)
    {
        if (template == null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        arguments ??= CommandArguments.Parse(string.Empty);
        // 只借用渲染器的参数替换规则；Body 由 template 直接传入。
        var command = new PluginCommand
        {
            Name = "workflow",
            ShortName = "workflow",
            ArgumentNames = parameters ?? Array.Empty<string>()
        };

        // 不提供 ShellExecutor：`!`cmd`` 原样保留，工作流脚本自己决定怎么处理。
        var rendered = CommandTemplateRenderer.RenderResult(template, command, new CommandRenderContext { Arguments = arguments });
        return CommandTemplateRenderer.AppendArgumentsIfUnused(rendered.Text, arguments, rendered.ArgumentsConsumed);
    }

    private sealed class WorkflowExtension : IExtension
    {
        private readonly IReadOnlyList<WorkflowDefinition> _workflows;
        private readonly Func<WorkflowRuntimeOptions> _optionsFactory;
        private readonly string _name;

        public WorkflowExtension(
            IReadOnlyList<WorkflowDefinition> workflows,
            Func<WorkflowRuntimeOptions> optionsFactory,
            string name)
        {
            var unique = new List<WorkflowDefinition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var workflow in workflows)
            {
                if (workflow != null && seen.Add(workflow.Name))
                {
                    unique.Add(workflow);
                }
            }

            _workflows = unique;
            _optionsFactory = optionsFactory;
            _name = name;
        }

        public string Name => _name;

        public string? Description => "运行 JavaScript 工作流（agent 编排）。";

        public void Activate(IExtensionApi api)
        {
            foreach (var workflow in _workflows)
            {
                var captured = workflow;
                api.RegisterCommand(
                    workflow.Name,
                    string.IsNullOrWhiteSpace(workflow.Description) ? "工作流 " + workflow.Name : workflow.Description,
                    (context, arguments, token) => RunAsync(captured, context, arguments, token),
                    DescribeArgumentHint(workflow));
            }

            api.RegisterTool(
                ToolDefinition.Create(ListToolName, DescribeWorkflows()),
                (_, _, _) => Task.FromResult(DescribeWorkflowsJson()),
                ToolExposure.ModelOnly,
                readOnlyHint: true);
        }

        private static string? DescribeArgumentHint(WorkflowDefinition workflow)
            => workflow.Parameters.Count == 0
                ? null
                : string.Join(" ", workflow.Parameters.Select(parameter => "<" + parameter + ">"));

        private string DescribeWorkflows()
        {
            if (_workflows.Count == 0)
            {
                return "Lists the available workflows. None are loaded.";
            }

            var builder = new StringBuilder("Lists the available JavaScript workflows. Run one with /<name> <arguments>. Available:");
            foreach (var workflow in _workflows)
            {
                builder.Append(' ').Append('/').Append(workflow.Name);
            }

            return builder.ToString();
        }

        private string DescribeWorkflowsJson()
        {
            var items = new List<object>();
            foreach (var workflow in _workflows)
            {
                items.Add(new
                {
                    name = workflow.Name,
                    description = workflow.Description,
                    parameters = workflow.Parameters,
                    plugin = workflow.PluginName
                });
            }

            return JsonSerializer.Serialize(new { workflows = items });
        }

        /// <summary>脚本返回单个字符串时直接显示其内容，其余结果按 JSON 原文显示。</summary>
        private static string DisplayText(string output)
        {
            if (!output.StartsWith("\"", StringComparison.Ordinal))
            {
                return output;
            }

            try
            {
                using var document = JsonDocument.Parse(output);
                return document.RootElement.ValueKind == JsonValueKind.String
                    ? document.RootElement.GetString() ?? output
                    : output;
            }
            catch (JsonException)
            {
                return output;
            }
        }

        private async Task<CommandResult> RunAsync(
            WorkflowDefinition workflow,
            CommandContext context,
            CommandArguments arguments,
            CancellationToken cancellationToken)
        {
            var options = _optionsFactory() ?? new WorkflowRuntimeOptions();
            options.WorkingDirectory ??= context.WorkingDirectory;
            options.Log ??= message => context.NotifyUser(message, ExtensionNoticeKind.Info);

            var rendered = RenderArguments(workflow.ArgumentTemplate, arguments, workflow.Parameters);
            var runtime = new WorkflowRuntime(options);
            WorkflowRunResult result;
            try
            {
                result = await runtime.RunAsync(workflow, rendered, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.NotifyUser("/" + workflow.Name + " 运行失败：" + ex.Message, ExtensionNoticeKind.Warning);
                return CommandResult.Display("/" + workflow.Name + " 运行失败：" + ex.Message);
            }

            if (!result.Ok)
            {
                var failure = "/" + workflow.Name + " 运行失败：" + (result.Error ?? "未知错误");
                context.NotifyUser(failure, ExtensionNoticeKind.Warning);
                return CommandResult.Display(failure);
            }

            var text = string.IsNullOrWhiteSpace(result.Output)
                ? "/" + workflow.Name + " 已完成，没有返回值。"
                : DisplayText(result.Output!);

            context.NotifyUser(
                "/" + workflow.Name + " 完成（" + result.Duration.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " 秒，" +
                result.AgentCalls.ToString(CultureInfo.InvariantCulture) + " 次 agent、" +
                result.ToolCalls.ToString(CultureInfo.InvariantCulture) + " 次 tool）",
                ExtensionNoticeKind.Success);

            return CommandResult.Display(text);
        }
    }
}
