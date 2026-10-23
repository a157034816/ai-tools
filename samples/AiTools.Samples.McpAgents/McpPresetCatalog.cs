using System;
using System.Collections.Generic;

namespace AiTools.Samples.McpAgents;

/// <summary>大多数 agent 均可安装的常用 MCP 服务内置目录。</summary>
public static class McpPresetCatalog
{
    /// <summary>获取默认可用的预设，按名称排序。</summary>
    public static IReadOnlyList<McpPreset> Presets { get; } = new McpPreset[]
    {
        new()
        {
            Id = "filesystem",
            DisplayName = "Filesystem",
            Description = "读写本机指定目录中的文件（官方参考实现）",
            Transport = "stdio",
            Command = "npx",
            Arguments = new[] { "-y", "@modelcontextprotocol/server-filesystem", "." }
        },
        new()
        {
            Id = "memory",
            DisplayName = "Memory",
            Description = "基于知识图谱的持久记忆（官方参考实现）",
            Transport = "stdio",
            Command = "npx",
            Arguments = new[] { "-y", "@modelcontextprotocol/server-memory" }
        },
        new()
        {
            Id = "sequential-thinking",
            DisplayName = "Sequential Thinking",
            Description = "帮助模型进行结构化、逐步推理（官方参考实现）",
            Transport = "stdio",
            Command = "npx",
            Arguments = new[] { "-y", "@modelcontextprotocol/server-sequential-thinking" }
        },
        new()
        {
            Id = "context7",
            DisplayName = "Context7",
            Description = "查询最新版库文档与代码示例（Upstash）",
            Transport = "stdio",
            Command = "npx",
            Arguments = new[] { "-y", "@upstash/context7-mcp" }
        },
        new()
        {
            Id = "github",
            DisplayName = "GitHub",
            Description = "GitHub 仓库、Issue 与 PR 操作，需要 GitHub Personal Access Token",
            Transport = "stdio",
            Command = "npx",
            Arguments = new[] { "-y", "@modelcontextprotocol/server-github" },
            RequiredEnvironment = new Dictionary<string, string>
            {
                ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "GitHub Personal Access Token（repo 权限）"
            }
        },
        new()
        {
            Id = "playwright",
            DisplayName = "Playwright",
            Description = "浏览器自动化：导航、点击、截图、读取页面（Microsoft）",
            Transport = "stdio",
            Command = "npx",
            Arguments = new[] { "-y", "@playwright/mcp@latest" }
        },
        new()
        {
            Id = "puppeteer",
            DisplayName = "Puppeteer",
            Description = "无头浏览器控制与网页抓取（官方参考实现）",
            Transport = "stdio",
            Command = "npx",
            Arguments = new[] { "-y", "@modelcontextprotocol/server-puppeteer" }
        },
        new()
        {
            Id = "fetch",
            DisplayName = "Fetch",
            Description = "抓取 URL 并转换为 Markdown（官方参考实现）",
            Transport = "stdio",
            Command = "uvx",
            Arguments = new[] { "mcp-server-fetch" }
        },
        new()
        {
            Id = "git",
            DisplayName = "Git",
            Description = "读取、搜索与操作本地 Git 仓库（Python 参考实现）",
            Transport = "stdio",
            Command = "uvx",
            Arguments = new[] { "mcp-server-git", "--repository", "." }
        },
        new()
        {
            Id = "sqlite",
            DisplayName = "SQLite",
            Description = "查询与操作 SQLite 数据库（官方参考实现）",
            Transport = "stdio",
            Command = "uvx",
            Arguments = new[] { "mcp-server-sqlite", "--db-path", "database.db" }
        },
        new()
        {
            Id = "fetch-http",
            DisplayName = "Fetch (HTTP)",
            Description = "通过 Streamable HTTP 连接的示例远程 MCP 服务",
            Transport = "http",
            Url = "https://api.example.com/mcp"
        }
    };

    /// <summary>按 ID 查找预设（不区分大小写）。</summary>
    public static McpPreset? Find(string id)
    {
        foreach (var preset in Presets)
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return null;
    }
}
