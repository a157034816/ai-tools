using AiTools.Abstractions;
using AiTools.Anthropic;
using AiTools.Gemini;
using AiTools.OpenAI;

namespace AiTools.Samples.Console;

/// <summary>按环境变量创建第一个可用的 Provider 会话。</summary>
internal sealed class DemoSession : IDisposable
{
    private DemoSession(string providerName, string chatModel, string embeddingModel, IChatClient chat, IEmbeddingClient? embeddings)
    {
        ProviderName = providerName;
        ChatModel = chatModel;
        EmbeddingModel = embeddingModel;
        Chat = chat;
        Embeddings = embeddings;
    }

    public string ProviderName { get; }

    public string ChatModel { get; }

    public string EmbeddingModel { get; }

    public IChatClient Chat { get; }

    public IEmbeddingClient? Embeddings { get; }

    private static bool _notified;

    /// <summary>按 OpenAI → Anthropic → Gemini 的顺序探测可用环境变量。</summary>
    public static DemoSession? TryCreate(bool requireChat = true)
    {
        var openAiKey = Demo.Env("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            var options = new OpenAiOptions
            {
                ApiKey = openAiKey,
                BaseAddress = Demo.Env("OPENAI_BASE_URL") is { Length: > 0 } url ? new Uri(url) : null
            };

            var client = new OpenAiClient(options);
            var chatModel = Demo.Env("OPENAI_CHAT_MODEL") ?? "gpt-4.1-mini";
            var embeddingModel = Demo.Env("OPENAI_EMBEDDING_MODEL") ?? "text-embedding-3-small";
            return new DemoSession("openai", chatModel, embeddingModel, client.Chat, client.Embeddings);
        }

        var anthropicKey = Demo.Env("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(anthropicKey))
        {
            var client = new AnthropicClient(new AnthropicOptions { ApiKey = anthropicKey });
            var chatModel = Demo.Env("ANTHROPIC_CHAT_MODEL") ?? "claude-sonnet-4-5";
            return new DemoSession("anthropic", chatModel, "(不支持嵌入)", client.Chat, null);
        }

        var geminiKey = Demo.Env("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(geminiKey))
        {
            var client = new GeminiClient(new GeminiOptions { ApiKey = geminiKey });
            var chatModel = Demo.Env("GEMINI_CHAT_MODEL") ?? "gemini-2.5-flash";
            var embeddingModel = Demo.Env("GEMINI_EMBEDDING_MODEL") ?? "text-embedding-004";
            return new DemoSession("gemini", chatModel, embeddingModel, client.Chat, client.Embeddings);
        }

        if (!_notified)
        {
            _notified = true;
            Demo.Note("未检测到 OPENAI_API_KEY / ANTHROPIC_API_KEY / GEMINI_API_KEY，聊天相关演示将跳过。");
        }

        return null;
    }

    public void Dispose()
    {
    }
}
