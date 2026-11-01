using System;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Anthropic;
using AiTools.Configuration;
using Xunit;

namespace AiTools.Tests.Core;

/// <summary>
/// 验证宿主配置能真正传到 provider：工厂按开关接线 Anthropic 缓存标记，
/// 设置能往返持久化，且关闭后不再写入任何缓存字段。
/// </summary>
public class PromptCacheConfigurationTests
{
    private static ProviderConfig AnthropicProvider() => new()
    {
        Id = "anthropic",
        DisplayName = "Anthropic",
        Protocol = ProviderProtocols.Anthropic,
        BaseUrl = "https://api.anthropic.com/v1",
        ApiKey = "test-key",
        Models = { new ModelConfig { Id = "claude-sonnet-4-5" } }
    };

    [Fact]
    public void Factory_EnabledPromptCache_WiresAnthropicCaching()
    {
        var factory = new BuiltInProviderFactory(new PromptCacheOptions { Enabled = true });

        var client = Assert.IsType<AnthropicChatClient>(factory.CreateChatClient(AnthropicProvider()));

        var options = ReadOptions(client);
        Assert.NotNull(options.PromptCaching);
        Assert.True(options.PromptCaching!.CacheSystem);
        Assert.True(options.PromptCaching.CacheMessages);
    }

    [Fact]
    public void Factory_DisabledPromptCache_LeavesCachingOff()
    {
        var factory = new BuiltInProviderFactory(new PromptCacheOptions { Enabled = false });

        var client = Assert.IsType<AnthropicChatClient>(factory.CreateChatClient(AnthropicProvider()));

        var options = ReadOptions(client);
        // 关闭时客户端不发送 cache_control，行为与引入预热机制之前一致。
        Assert.Null(options.PromptCaching);
    }

    [Fact]
    public void ToAnthropicCaching_MapsEverySwitch()
    {
        var options = BuiltInProviderFactory.ToAnthropicCaching(new PromptCacheOptions
        {
            LongRetention = true,
            CacheTools = false,
            CacheSystem = false,
            CacheMessages = false,
            MessageCacheOffset = 3
        });

        Assert.True(options.UseLongRetention);
        Assert.False(options.CacheTools);
        Assert.False(options.CacheSystem);
        Assert.False(options.CacheMessages);
        Assert.Equal(3, options.MessageCacheOffset);
    }

    [Fact]
    public void AppSettings_Clone_CopiesPromptCacheOptions()
    {
        var settings = new AppSettings
        {
            PromptCache = new PromptCacheOptions { LongRetention = true, MessageCacheOffset = 2, WarmingEnabled = false }
        };

        var clone = settings.Clone();
        clone.PromptCache.CacheSystem = false;

        // 深拷贝：修改克隆不影响原对象。
        Assert.True(settings.PromptCache.LongRetention);
        Assert.True(settings.PromptCache.CacheSystem);
        Assert.Equal(2, settings.PromptCache.MessageCacheOffset);
        Assert.False(settings.PromptCache.WarmingEnabled);
        Assert.False(clone.PromptCache.CacheSystem);
    }

    [Fact]
    public async Task SettingsStore_RoundTripsPromptCacheOptions()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "aitools-cache-" + Guid.NewGuid().ToString("N"),
            "settings.json");
        try
        {
            var store = new JsonFileSettingsStore(path, new TestProtector());
            await store.SaveAsync(new AppSettings
            {
                PromptCache = new PromptCacheOptions
                {
                    Enabled = true,
                    WarmingEnabled = true,
                    LongRetention = true,
                    CacheTools = false,
                    MessageCacheOffset = 1
                }
            });

            var loaded = await store.LoadAsync();

            Assert.True(loaded.PromptCache.LongRetention);
            Assert.False(loaded.PromptCache.CacheTools);
            Assert.Equal(1, loaded.PromptCache.MessageCacheOffset);
        }
        finally
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (dir != null && System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>读取客户端实际持有的选项。仅用于验证工厂的接线，不参与生产路径。</summary>
    private static AnthropicOptions ReadOptions(AnthropicChatClient client)
    {
        var field = typeof(AnthropicChatClient).GetField(
            "_options",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<AnthropicOptions>(field!.GetValue(client));
    }

    private sealed class TestProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedText) => protectedText;
    }
}
