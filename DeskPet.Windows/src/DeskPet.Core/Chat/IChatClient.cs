namespace DeskPet.Core.Chat;

/// <summary>
/// OpenAI 兼容对话客户端，对应 Swift <c>PetChatManager</c> 的对话能力
/// （<c>send</c> 流式对话 + <c>testConnection</c>）。
/// </summary>
public interface IChatClient
{
    /// <summary>
    /// 流式发送用户输入并返回完整回复，增量经 <paramref name="progress"/> 上报
    /// （对应 Swift <c>PetChatManager.send</c> 的 SSE 流式累积）。
    /// </summary>
    /// <param name="history">历史消息（含系统/用户/助手消息，由调用方裁剪）。</param>
    /// <param name="userInput">本次用户输入（追加在历史之后）。</param>
    /// <param name="progress">流式增量回调，可为 null。</param>
    /// <param name="ct">取消令牌。</param>
    Task<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        string userInput,
        IProgress<string>? progress,
        CancellationToken ct);

    /// <summary>测试与 API 的连接（对应 Swift <c>PetChatManager.testConnection</c>）。</summary>
    Task<bool> TestConnectionAsync(CancellationToken ct);
}
