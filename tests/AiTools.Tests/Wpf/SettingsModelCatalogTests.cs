using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Wpf;
using AiTools.Wpf.Configuration;
using AiTools.Wpf.ViewModels;
using Xunit;

namespace AiTools.Tests.Wpf;

/// <summary>
/// 验证设置页「从服务商接口获取模型列表」的行为，包括获取、筛选、勾选与去重。
/// 与全局服务测试共用集合，因为需要初始化进程级的 <see cref="AiToolsWpf"/>。
/// </summary>
[Collection(AiToolsWpfGlobalCollection.Name)]
public sealed class SettingsModelCatalogTests : IDisposable
{
    private readonly FakeCatalogFactory _factory = new();
    private readonly InMemoryStore _store = new();

    public SettingsModelCatalogTests()
    {
        AiToolsWpf.Reset();
        AiToolsWpf.Initialize(options =>
        {
            options.SettingsStore = _store;
            options.ProviderFactory = new FixedProviderFactory();
            options.ProviderModelCatalogFactory = _factory;
        });
    }

    public void Dispose() => AiToolsWpf.Reset();

    private static ProviderConfig ProviderConfig(params string[] modelIds)
    {
        var config = new ProviderConfig
        {
            Id = "openai",
            DisplayName = "OpenAI",
            Protocol = ProviderProtocols.OpenAi,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test"
        };

        foreach (var id in modelIds)
        {
            config.Models.Add(new ModelConfig { Id = id, Enabled = true });
        }

        return config;
    }

    private static ProviderEditorViewModel Editor(params string[] modelIds)
        => new(ProviderConfig(modelIds));

    /// <summary>通过存储预置服务商，再初始化一个已加载的 ViewModel。</summary>
    private async Task<(SettingsViewModel ViewModel, ProviderEditorViewModel Editor)> LoadAsync(
        params string[] modelIds)
    {
        _store.Settings = new AppSettings
        {
            Version = 1,
            Providers = new List<ProviderConfig> { ProviderConfig(modelIds) }
        };

        var vm = new SettingsViewModel();
        await vm.InitializeAsync();

        var editor = Assert.Single(vm.Providers);
        vm.SelectedProvider = editor;
        return (vm, editor);
    }

    [Fact]
    public async Task FetchModels_PopulatesCandidatesAndPreselectsNewOnes()
    {
        _factory.Models = new[]
        {
            new ModelInfo { Id = "gpt-4.1", DisplayName = "GPT-4.1" },
            new ModelInfo { Id = "o3" }
        };

        var editor = Editor("gpt-4.1");
        var catalog = AiToolsWpf.CreateModelCatalog(editor.CreateProviderConfig());

        var models = await catalog.ListModelsAsync();

        editor.ApplyFetchedModels(models);
        editor.IsModelPickerOpen = true;

        Assert.True(editor.IsModelPickerOpen);
        Assert.Equal(2, editor.FetchedModels.Count);

        var existing = editor.FetchedModels.Single(m => m.Id == "gpt-4.1");
        Assert.True(existing.IsAlreadyConfigured);
        Assert.False(existing.IsSelected);

        var fresh = editor.FetchedModels.Single(m => m.Id == "o3");
        Assert.False(fresh.IsAlreadyConfigured);
        Assert.True(fresh.IsSelected);
        Assert.Equal("已选 1 / 2", editor.FetchedSelectionText);
    }

    [Fact]
    public void ApplySelectedFetchedModels_AddsOnlyNewModelsAndKeepsConfiguredRows()
    {
        var editor = Editor("gpt-4.1");
        editor.ApplyFetchedModels(new[]
        {
            new ModelInfo { Id = "gpt-4.1", DisplayName = "GPT-4.1" },
            new ModelInfo { Id = "o3", DisplayName = "o3" }
        });

        var added = editor.ApplySelectedFetchedModels();

        Assert.Equal(1, added);
        Assert.Equal(new[] { "gpt-4.1", "o3" }, editor.Models.Select(m => m.Id));
        Assert.Equal("o3", editor.Models[1].DisplayName);
        Assert.All(editor.FetchedModels, row => Assert.True(row.IsAlreadyConfigured));
        Assert.All(editor.FetchedModels, row => Assert.False(row.IsSelected));
    }

    [Fact]
    public void ModelFilter_MatchesIdAndDisplayName()
    {
        var editor = Editor();
        editor.ApplyFetchedModels(new[]
        {
            new ModelInfo { Id = "gpt-4.1", DisplayName = "GPT-4.1" },
            new ModelInfo { Id = "o3", DisplayName = "Reasoning" }
        });

        editor.ModelFilterText = "reason";
        Assert.Equal(new[] { "o3" }, editor.FetchedModels.Select(m => m.Id));

        editor.ModelFilterText = "GPT";
        Assert.Equal(new[] { "gpt-4.1" }, editor.FetchedModels.Select(m => m.Id));

        editor.ModelFilterText = string.Empty;
        Assert.Equal(2, editor.FetchedModels.Count);
    }

    [Fact]
    public void SetAllFetchedSelected_OnlyAffectsFilteredRows()
    {
        var editor = Editor();
        editor.ApplyFetchedModels(new[]
        {
            new ModelInfo { Id = "a-1" },
            new ModelInfo { Id = "b-1" }
        });

        editor.SetAllFetchedSelected(false);
        Assert.All(editor.FetchedModels, row => Assert.False(row.IsSelected));

        editor.ModelFilterText = "a-";
        editor.SetAllFetchedSelected(true);

        Assert.True(editor.FetchedModels.Single().IsSelected);
        Assert.False(editor.FetchedSelectionText.Length == 0);
    }

    [Fact]
    public void ChangingEndpoint_ClearsFetchedCandidates()
    {
        var editor = Editor();
        editor.ApplyFetchedModels(new[] { new ModelInfo { Id = "a" } });
        editor.IsModelPickerOpen = true;

        editor.BaseUrl = "https://gateway.example.com/v1";

        Assert.Empty(editor.FetchedModels);
        Assert.False(editor.HasFetchedModels);
        Assert.False(editor.IsModelPickerOpen);
    }

    [Fact]
    public void NewModelRow_RefreshesAlreadyConfiguredFlags()
    {
        var editor = Editor();
        editor.ApplyFetchedModels(new[] { new ModelInfo { Id = "o3" } });
        Assert.False(editor.FetchedModels[0].IsAlreadyConfigured);

        // 手动添加同名模型后，候选行的「已配置」标记应同步。
        editor.Models.Add(new ModelRowViewModel(editor, new ModelConfig { Id = "o3" }));

        Assert.True(editor.FetchedModels[0].IsAlreadyConfigured);
    }

    [Fact]
    public async Task SettingsViewModel_FetchCommand_ReportsErrorsFromFactory()
    {
        _factory.Error = new AiToolsWpfException("网关不支持 /models");

        var (vm, editor) = await LoadAsync();

        await vm.FetchModelsAsync(editor);

        Assert.Equal(ConnectionTestState.Failed, editor.ModelFetchState);
        Assert.Contains("网关不支持", editor.ModelFetchStatus);
        Assert.True(editor.IsModelPickerOpen);
        Assert.False(editor.IsFetchingModels);
    }

    [Fact]
    public async Task SettingsViewModel_FetchCommand_PopulatesCandidatesAndAppliesThem()
    {
        _factory.Models = new[] { new ModelInfo { Id = "gpt-4.1" }, new ModelInfo { Id = "o3" } };

        var (vm, editor) = await LoadAsync();
        await vm.FetchModelsAsync(editor);

        Assert.True(editor.IsModelPickerOpen);
        Assert.Equal(ConnectionTestState.Succeeded, editor.ModelFetchState);
        Assert.Equal(2, editor.FetchedModels.Count);

        vm.ApplyFetchedModelsCommand.Execute(editor);

        Assert.Equal(new[] { "gpt-4.1", "o3" }, editor.Models.Select(model => model.Id));
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task SettingsViewModel_FetchCommand_RejectsIncompleteConfiguration()
    {
        var (vm, editor) = await LoadAsync();
        editor.BaseUrl = string.Empty;

        await vm.FetchModelsAsync(editor);

        Assert.Equal(ConnectionTestState.Failed, editor.ModelFetchState);
        Assert.Contains("Base URL", editor.ModelFetchStatus);
        Assert.True(editor.IsModelPickerOpen);
    }

    [Fact]
    public void CreateModelCatalog_WhenFactoryUnsupported_ThrowsFriendlyError()
    {
        AiToolsWpf.Reset();
        AiToolsWpf.Initialize(options =>
        {
            options.SettingsStore = _store;
            options.ProviderFactory = new FixedProviderFactory();
        });

        var ex = Assert.Throws<AiToolsWpfException>(() => AiToolsWpf.CreateModelCatalog(new ProviderConfig
        {
            Protocol = ProviderProtocols.OpenAi,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "k"
        }));

        Assert.Contains("ProviderModelCatalogFactory", ex.Message);
        Assert.False(AiToolsWpf.SupportsModelCatalog);
    }

    [Fact]
    public void BuiltInFactory_ImplementsModelCatalogFactory()
    {
        var factory = new BuiltInProviderFactory();

        Assert.IsAssignableFrom<IProviderModelCatalogFactory>(factory);

        var catalog = factory.CreateModelCatalog(new ProviderConfig
        {
            Id = "deepseek",
            Protocol = ProviderProtocols.OpenAi,
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "k"
        });
        Assert.Equal("deepseek", catalog.ProviderName);

        var anthropic = factory.CreateModelCatalog(new ProviderConfig
        {
            Protocol = ProviderProtocols.Anthropic,
            BaseUrl = "https://api.anthropic.com/v1",
            ApiKey = "k"
        });
        Assert.Equal("anthropic", anthropic.ProviderName);

        var gemini = factory.CreateModelCatalog(new ProviderConfig
        {
            Protocol = ProviderProtocols.Gemini,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            ApiKey = "k"
        });
        Assert.Equal("gemini", gemini.ProviderName);
    }

    [Fact]
    public void BuiltInFactory_UnknownProtocol_Throws()
    {
        var factory = new BuiltInProviderFactory();

        Assert.Throws<AiToolsWpfException>(() => factory.CreateModelCatalog(new ProviderConfig
        {
            Protocol = "unknown",
            BaseUrl = "https://x",
            ApiKey = "k"
        }));
    }

    private sealed class FakeCatalogFactory : IProviderModelCatalogFactory
    {
        public IReadOnlyList<ModelInfo> Models { get; set; } = Array.Empty<ModelInfo>();

        public Exception? Error { get; set; }

        public IChatClient? Client { get; set; }

        public IModelCatalog CreateModelCatalog(ProviderConfig provider) => new FakeCatalog(this);

        private sealed class FakeCatalog : IModelCatalog
        {
            private readonly FakeCatalogFactory _owner;

            public FakeCatalog(FakeCatalogFactory owner) => _owner = owner;

            public string ProviderName => "fake";

            public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            {
                if (_owner.Error != null)
                {
                    throw _owner.Error;
                }

                return Task.FromResult(_owner.Models);
            }
        }
    }

    private sealed class FixedProviderFactory : IProviderFactory
    {
        public IChatClient CreateChatClient(ProviderConfig provider) => new NoopChatClient();
    }

    private sealed class NoopChatClient : IChatClient
    {
        public string ProviderName => "noop";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse
            {
                Message = ChatMessage.Assistant(string.Empty),
                FinishReason = ChatFinishReason.Stop
            });

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private sealed class InMemoryStore : ISettingsStore
    {
        public AppSettings Settings { get; set; } = new() { Version = 1 };

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Settings.Clone());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings.Clone();
            return Task.CompletedTask;
        }
    }
}
