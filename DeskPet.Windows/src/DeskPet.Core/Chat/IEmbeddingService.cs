namespace DeskPet.Core.Chat;

/// <summary>
/// 文本向量化服务（RAG 用），对应 Swift <c>PetChatManager.embed</c>
/// （OpenAI 兼容 <c>/embeddings</c> 接口，批量生成）。
/// </summary>
public interface IEmbeddingService
{
    /// <summary>批量生成文本向量，返回与输入一一对应的向量列表（对应 Swift <c>PetChatManager.embed</c>）。</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
