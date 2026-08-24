using DeskPet.Core.RAG;
using DeskPet.Core.Services;

namespace DeskPet.Core.Chat;

/// <summary>
/// 对话前 RAG 检索 → 注入 system 上下文的装饰器管线（Task 7，Swift
/// <c>PetChatManager.send</c> 124-134 RAG 块 + <c>VectorStoreManager.contextPrompt</c> 82-95 移植）：
/// <c>RagEnabled</c> 时先对用户输入生成向量 → 向量库检索 topK（Swift 默认 3）→
/// 非空结果按 Swift 格式组装为一条 <c>ChatRole.System</c> 消息 PREPEND 到历史首位再交给内层客户端
/// （内层先发设置 system prompt，再按序发历史——注入消息得以进入请求体）。
/// 检索失败（embedding/search 抛异常或空结果）静默跳过、不影响对话（Swift <c>try?</c> 语义，不记日志）。
/// </summary>
public sealed class RagChatPipeline : IChatClient
{
    /// <summary>参考上下文字符预算（Swift <c>contextPrompt</c> 默认 maxCharacters = 2000）。</summary>
    public const int MaxContextCharacters = 2000;

    /// <summary>参考上下文前缀句（Swift VectorStoreManager.swift 93 逐字移植）。</summary>
    public const string ContextPrefix = "以下是宠物主人提供的参考资料（可能与问题相关，也可能无关，请按需采用，不要编造）：\n";

    private const string Bullet = "• ";

    private readonly IChatClient _inner;
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _store;
    private readonly ISettingsService _settings;

    /// <param name="inner">内层对话客户端（生产环境为 PetChatClient；其自身同时充当 IEmbeddingService，与 <paramref name="embeddings"/> 为同一实例）。</param>
    /// <param name="embeddings">向量化服务（RAG 检索用）。</param>
    /// <param name="store">向量库（与 RAGManager 共用同一实例，保证导入的文档可被检索）。</param>
    /// <param name="settings">设置服务（读 <c>RagEnabled</c> 开关与 <c>ChatHistoryCount</c> 容量）。</param>
    public RagChatPipeline(
        IChatClient inner, IEmbeddingService embeddings, IVectorStore store, ISettingsService settings)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// 流式发送：<c>RagEnabled</c> 时先检索并注入上下文，否则原样透传（Ruling 4）。
    /// 注入的 system 消息置于历史首位，并预留容量（见 <c>MakeRoomInHistory</c>）。
    /// </summary>
    public async Task<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        string userInput,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var context = _settings.RagEnabled ? await TryRetrieveContextAsync(userInput, ct) : null;
        if (context is null)
        {
            return await _inner.StreamChatAsync(history, userInput, progress, ct);
        }

        var augmented = new List<ChatMessage>(history.Count + 1)
        {
            new(Guid.NewGuid(), ChatRole.System, context, IsStreaming: false),
        };
        augmented.AddRange(MakeRoomInHistory(history));
        return await _inner.StreamChatAsync(augmented, userInput, progress, ct);
    }

    /// <summary>测试连接原样委托内层客户端（RAG 与连接测试无关）。</summary>
    public Task<bool> TestConnectionAsync(CancellationToken ct) => _inner.TestConnectionAsync(ct);

    /// <summary>
    /// 检索并组装上下文；embedding/search 抛异常或空结果均返回 null（Swift <c>try?</c> 语义：
    /// 检索失败不打断对话，静默透传、不记日志）。
    /// </summary>
    private async Task<string?> TryRetrieveContextAsync(string userInput, CancellationToken ct)
    {
        try
        {
            var embeddings = await _embeddings.EmbedAsync(new[] { userInput }, ct);
            if (embeddings.Count == 0) return null;
            var results = await _store.SearchAsync(userInput, embeddings[0], RAGManager.DefaultTopK, ct);
            return BuildContextPrompt(results);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 检索结果 → system 上下文文本（Swift <c>contextPrompt</c> 82-95 逐行移植）：
    /// 前缀句 + <c>"• "</c> 逐条（每条内换行 → 空格），总字符预算 2000
    /// （超出剩余预算的条目丢弃，含首条超预算 → 整体返回 null）；无条目返回 null。
    /// </summary>
    public static string? BuildContextPrompt(IReadOnlyList<VectorSearchResult> results)
    {
        if (results.Count == 0) return null;
        var remaining = MaxContextCharacters;
        var lines = new List<string>();
        foreach (var result in results)
        {
            var line = Bullet + result.Text.Replace("\n", " ");
            if (line.Length > remaining) break;
            lines.Add(line);
            remaining -= line.Length + 1;
        }
        return lines.Count == 0 ? null : ContextPrefix + string.Join("\n", lines);
    }

    /// <summary>
    /// 内层客户端按 <c>max(1, ChatHistoryCount)</c> 做 suffix 裁剪；注入消息在历史首位，
    /// 若不预留容量，长会话时会被裁掉——这里把历史裁剪到最多 <c>maxHistory - 1</c> 条，
    /// 保证注入消息始终进入请求体（对应 Swift 中 ragContext 独立于历史、永不裁剪）。
    /// </summary>
    private IReadOnlyList<ChatMessage> MakeRoomInHistory(IReadOnlyList<ChatMessage> history)
    {
        var maxHistory = Math.Max(1, _settings.ChatHistoryCount);
        var keep = Math.Max(0, maxHistory - 1);
        return history.Count > keep
            ? history.Skip(history.Count - keep).ToList()
            : history;
    }
}
