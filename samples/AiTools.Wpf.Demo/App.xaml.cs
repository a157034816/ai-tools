using System;
using System.IO;
using System.Windows;
using AiTools.Abstractions;
using AiTools.Core;
using AiTools.Core.Sessions;
using AiTools.Skills;
using AiTools.Wpf;
using AiTools.Configuration;
using AiTools.Wpf.Skills;

namespace AiTools.Wpf.Demo;

/// <summary>演示宿主：展示如何通过全局配置与接口实现注册 AiTools.Wpf。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 1) 发现示例技能（samples/AiTools.Wpf.Demo/skills 下的 SKILL.md 包）。
        var skillsDirectory = Path.Combine(AppContext.BaseDirectory, "skills");
        var skillCatalog = SkillCatalog.LoadFromDirectory(skillsDirectory);
        var skillToolset = new SkillToolset(skillCatalog);
        var basePrompt = "你是简洁、准确的中文技术助手。回答尽量控制在 5 句话以内。";

        // 2) 全局配置：设置存储位置、技能目录、自定义工具、回调。
        // 演示用独立子目录（~/.ai-tools/wpf-demo），避免与真实应用的设置互相覆盖。
        var demoRoot = Path.Combine(AiToolsPaths.GetRootDirectory(), "wpf-demo");
        var settingsPath = Path.Combine(demoRoot, "settings.json");

        AiToolsWpf.Initialize(new AiToolsWpfOptions
        {
            // 演示：把设置文件放到独立目录，避免与真实应用互相覆盖。
            SettingsStore = new JsonFileSettingsStore(settingsPath, new DpapiSecretProtector()),

            // 演示：会话历史也放到独立目录，/resume 与 /fork 使用。
            ConversationStore = new JsonConversationStore(demoRoot),
            AutoSaveConversations = true,

            // /ask、/goal 与 /plan 由内置扩展（AskExtension / GoalExtension / PlanExtension）提供；
            // 如果宿主另有 Claude Code 插件目录，设置 PluginsDirectory 即可同时启用插件命令。
            // 模型调用 ask 时，对话页会把选项面板叠在输入框上方（默认的 InlineAskPresenter）；
            // 想改用弹窗或别的呈现方式，设置 options.AskPresenter 即可。
            EnableExtensions = true,

            // 技能目录同时放进系统提示词（渐进式披露：只注入名称与描述）。
            DefaultSystemPrompt = skillToolset.HasSkills
                ? basePrompt + "\n\n" + skillToolset.BuildSystemPrompt()
                : basePrompt,
            EnableTools = true,
            MaxToolRounds = 6,

            // 演示：拦截每次请求，打印到调试输出。
            RequestBuilder = context =>
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AiTools] {context.Provider.DisplayName} / {context.Model.Id} · {context.Messages.Count} 条消息 · {context.Tools.Count} 个工具 · {skillCatalog.Count} 个技能");
                return null; // 返回 null 使用默认构建逻辑
            },

            OnTurnCompleted = context =>
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AiTools] 轮次完成，用时 {context.Duration.TotalMilliseconds:F0} ms，tokens {context.Response.Usage?.TotalTokens ?? 0}");
            }
        });

        // 3) 接口实现注册：宿主工具 + 技能工具（模型可调用）。
        AiToolsWpf.RegisterToolProvider(new DemoToolProvider());
        if (skillToolset.HasSkills)
        {
            AiToolsWpf.RegisterToolProvider(new SkillChatToolProvider(skillToolset));
        }

        base.OnStartup(e);
    }
}
