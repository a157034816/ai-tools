using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Wpf;
using AiTools.Configuration;
using Xunit;

namespace AiTools.Tests.Wpf;

public class JsonFileSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "aitools-tests-" + Guid.NewGuid().ToString("N"), "settings.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir != null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static AppSettings SampleSettings() => new()
    {
        Providers =
        {
            new ProviderConfig
            {
                Id = "openai",
                DisplayName = "OpenAI",
                Protocol = ProviderProtocols.OpenAi,
                BaseUrl = "https://api.openai.com/v1",
                ApiKey = "sk-secret",
                Models = { new ModelConfig { Id = "gpt-4.1", DisplayName = "GPT-4.1" } }
            }
        },
        SelectedProviderId = "openai",
        SelectedModelId = "gpt-4.1",
        SystemPrompt = "be nice"
    };

    [Fact]
    public async Task LoadAsync_WhenFileMissing_ReturnsEmptySettings()
    {
        var store = new JsonFileSettingsStore(_path, new PassThroughProtector());

        var settings = await store.LoadAsync();

        Assert.Equal(0, settings.Version);
        Assert.Empty(settings.Providers);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var store = new JsonFileSettingsStore(_path, new PassThroughProtector());
        var original = SampleSettings();

        await store.SaveAsync(original);
        var loaded = await store.LoadAsync();

        Assert.Single(loaded.Providers);
        var provider = loaded.Providers[0];
        Assert.Equal("openai", provider.Id);
        Assert.Equal("sk-secret", provider.ApiKey);
        Assert.Equal("https://api.openai.com/v1", provider.BaseUrl);
        Assert.Equal("GPT-4.1", provider.Models[0].DisplayName);
        Assert.Equal("openai", loaded.SelectedProviderId);
        Assert.Equal("be nice", loaded.SystemPrompt);
    }

    [Fact]
    public async Task SaveAsync_ProtectsApiKeyOnDisk()
    {
        var protector = new PrefixProtector();
        var store = new JsonFileSettingsStore(_path, protector);
        await store.SaveAsync(SampleSettings());

        var raw = await File.ReadAllTextAsync(_path);
        Assert.Contains("protected:sk-secret", raw);
        Assert.DoesNotContain("\"sk-secret\"", raw);
    }

    [Fact]
    public async Task LoadAsync_UnprotectsApiKey()
    {
        var store = new JsonFileSettingsStore(_path, new PrefixProtector());
        await store.SaveAsync(SampleSettings());

        var loaded = await store.LoadAsync();

        Assert.Equal("sk-secret", loaded.Providers[0].ApiKey);
    }

    [Fact]
    public async Task SaveAsync_IsAtomic_LeavesNoTempFile()
    {
        var store = new JsonFileSettingsStore(_path, new PassThroughProtector());
        await store.SaveAsync(SampleSettings());
        await store.SaveAsync(SampleSettings());

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public async Task LoadAsync_InvalidJson_ThrowsFriendlyException()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, "{ not json");
        var store = new JsonFileSettingsStore(_path, new PassThroughProtector());

        var ex = await Assert.ThrowsAsync<AiToolsConfigurationException>(() => store.LoadAsync());
        Assert.Contains("设置文件", ex.Message);
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedText) => protectedText;
    }

    private sealed class PrefixProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;

        public string Unprotect(string protectedText)
            => protectedText.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedText.Substring("protected:".Length)
                : protectedText;
    }
}

public class DpapiSecretProtectorTests
{
    [Fact]
    public void ProtectThenUnprotect_RoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiSecretProtector();
        var protectedValue = protector.Protect("sk-123");

        Assert.StartsWith("dpapi:", protectedValue);
        Assert.Equal("sk-123", protector.Unprotect(protectedValue));
    }

    [Fact]
    public void Unprotect_PlaintextValue_ReturnsAsIs()
    {
        var protector = new DpapiSecretProtector();

        Assert.Equal("plain", protector.Unprotect("plain"));
    }

    [Fact]
    public void Unprotect_CorruptValue_ReturnsEmpty()
    {
        var protector = new DpapiSecretProtector();

        Assert.Equal(string.Empty, protector.Unprotect("dpapi:not-base64!!"));
    }

    [Fact]
    public void Protect_EmptyString_ReturnsEmpty()
    {
        var protector = new DpapiSecretProtector();

        Assert.Equal(string.Empty, protector.Protect(string.Empty));
    }
}

public class ProviderConfigTests
{
    [Fact]
    public void IsUsable_RequiresBaseUrlAndKeyUnlessLocal()
    {
        Assert.False(new ProviderConfig { BaseUrl = "https://x", ApiKey = "" }.IsUsable());
        Assert.True(new ProviderConfig { BaseUrl = "https://x", ApiKey = "k" }.IsUsable());
        Assert.True(new ProviderConfig { BaseUrl = "http://localhost:11434/v1", ApiKey = "" }.IsUsable());
        Assert.True(new ProviderConfig { BaseUrl = "http://127.0.0.1:8080/v1", ApiKey = "" }.IsUsable());
        Assert.False(new ProviderConfig { BaseUrl = "https://x", ApiKey = "k", Enabled = false }.IsUsable());
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var original = new ProviderConfig
        {
            Id = "p",
            Models = { new ModelConfig { Id = "m", DisplayName = "M" } }
        };

        var copy = original.Clone();
        copy.Models[0].Id = "changed";

        Assert.Equal("m", original.Models[0].Id);
    }

    [Fact]
    public void GetDisplayName_FallsBackToId()
    {
        Assert.Equal("m1", new ModelConfig { Id = "m1" }.GetDisplayName());
        Assert.Equal("Name", new ModelConfig { Id = "m1", DisplayName = "Name" }.GetDisplayName());
    }

    [Fact]
    public void FindProviderAndModel_AreCaseInsensitive()
    {
        var settings = new AppSettings
        {
            Providers = { new ProviderConfig { Id = "OpenAI", Models = { new ModelConfig { Id = "GPT-4.1" } } } }
        };

        Assert.NotNull(settings.FindProvider("openai"));
        Assert.NotNull(settings.FindProvider("OPENAI")!.FindModel("gpt-4.1"));
    }
}

public class BuiltInProviderFactoryTests
{
    [Fact]
    public void CreateChatClient_OpenAiCompatible_UsesCustomName()
    {
        var factory = new BuiltInProviderFactory();

        var client = factory.CreateChatClient(new ProviderConfig
        {
            Id = "deepseek",
            DisplayName = "DeepSeek",
            Protocol = ProviderProtocols.OpenAi,
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "k"
        });

        Assert.Equal("deepseek", client.ProviderName);
    }

    [Fact]
    public void CreateChatClient_AnthropicAndGemini()
    {
        var factory = new BuiltInProviderFactory();

        var anthropic = factory.CreateChatClient(new ProviderConfig
        {
            Protocol = ProviderProtocols.Anthropic,
            BaseUrl = "https://api.anthropic.com/v1",
            ApiKey = "k"
        });
        var gemini = factory.CreateChatClient(new ProviderConfig
        {
            Protocol = ProviderProtocols.Gemini,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            ApiKey = "k"
        });

        Assert.Equal("anthropic", anthropic.ProviderName);
        Assert.Equal("gemini", gemini.ProviderName);
    }

    [Fact]
    public void CreateChatClient_CachesByConfiguration()
    {
        var factory = new BuiltInProviderFactory();
        var config = new ProviderConfig
        {
            Protocol = ProviderProtocols.OpenAi,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "k"
        };

        Assert.Same(factory.CreateChatClient(config), factory.CreateChatClient(config));
    }

    [Fact]
    public void CreateChatClient_MissingBaseUrl_Throws()
    {
        var factory = new BuiltInProviderFactory();

        Assert.Throws<AiToolsConfigurationException>(() => factory.CreateChatClient(new ProviderConfig
        {
            Protocol = ProviderProtocols.OpenAi,
            ApiKey = "k"
        }));
    }

    [Fact]
    public void CreateChatClient_UnknownProtocol_Throws()
    {
        var factory = new BuiltInProviderFactory();

        var ex = Assert.Throws<AiToolsConfigurationException>(() => factory.CreateChatClient(new ProviderConfig
        {
            Protocol = "unknown",
            BaseUrl = "https://x",
            ApiKey = "k"
        }));

        Assert.Contains("不支持", ex.Message);
    }
}

/// <summary>
/// 供所有会修改进程级 <see cref="AiToolsWpf"/> 静态状态的测试类共用，
/// 包括聊天页面会话测试。禁用该集合内的并行化。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AiToolsWpfGlobalCollection
{
    public const string Name = "AiToolsWpfGlobal";
}

[Collection(AiToolsWpfGlobalCollection.Name)]
public class AiToolsWpfGlobalTests : IDisposable
{
    public AiToolsWpfGlobalTests() => AiToolsWpf.Reset();

    public void Dispose() => AiToolsWpf.Reset();

    [Fact]
    public void Options_BeforeInitialize_ThrowsFriendlyMessage()
    {
        var ex = Assert.Throws<AiToolsWpfException>(() => _ = AiToolsWpf.Options);
        Assert.Contains("Initialize", ex.Message);
    }

    [Fact]
    public void Initialize_AppliesDefaults()
    {
        AiToolsWpf.Initialize();

        Assert.True(AiToolsWpf.IsInitialized);
        Assert.NotNull(AiToolsWpf.Options.SettingsStore);
        Assert.NotNull(AiToolsWpf.Options.ProviderFactory);
        Assert.NotNull(AiToolsWpf.Options.SecretProtector);
        Assert.NotEmpty(AiToolsWpf.ProviderDescriptors);
    }

    [Fact]
    public void Initialize_Twice_Throws()
    {
        AiToolsWpf.Initialize();

        Assert.Throws<AiToolsWpfException>(() => AiToolsWpf.Initialize());
    }

    [Fact]
    public void Initialize_WithCallback_ConfiguresOptions()
    {
        AiToolsWpf.Initialize(options => options.DefaultSystemPrompt = "hello");

        Assert.Equal("hello", AiToolsWpf.Options.DefaultSystemPrompt);
    }

    [Fact]
    public async Task GetSettingsAsync_CachesUntilSaved()
    {
        var store = new InMemoryStore();
        AiToolsWpf.Initialize(options => options.SettingsStore = store);

        var first = await AiToolsWpf.GetSettingsAsync();
        store.Settings.Providers.Add(new ProviderConfig { Id = "changed" });
        var second = await AiToolsWpf.GetSettingsAsync();

        Assert.Same(first, second);
        Assert.Empty(second.Providers);

        await AiToolsWpf.SaveSettingsAsync(new AppSettings { Version = 1 });
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, store.Settings.Version);
    }

    [Fact]
    public void RegisterToolProvider_DeduplicatesOnReset()
    {
        AiToolsWpf.Initialize();
        AiToolsWpf.RegisterToolProvider(new NoopToolProvider());
        Assert.Single(AiToolsWpf.GetToolProviders());

        AiToolsWpf.Reset();
        AiToolsWpf.Initialize();
        Assert.Empty(AiToolsWpf.GetToolProviders());
    }

    private sealed class InMemoryStore : ISettingsStore
    {
        private AppSettings _settings = new();

        /// <summary>当前已保存的设置（测试可直接修改）。</summary>
        public AppSettings Settings
        {
            get => _settings;
            set => _settings = value;
        }

        public int SaveCount { get; private set; }

        // 与真实存储一致：每次读取返回独立副本，避免缓存与存储共享同一引用。
        public Task<AppSettings> LoadAsync(System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(_settings.Clone());

        public Task SaveAsync(AppSettings settings, System.Threading.CancellationToken cancellationToken = default)
        {
            _settings = settings.Clone();
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class NoopToolProvider : IChatToolProvider
    {
        public System.Collections.Generic.IReadOnlyList<ToolDefinition> GetTools()
            => Array.Empty<ToolDefinition>();

        public Task<string> InvokeAsync(ToolCall call, System.Threading.CancellationToken cancellationToken)
            => Task.FromResult("{}");
    }
}
