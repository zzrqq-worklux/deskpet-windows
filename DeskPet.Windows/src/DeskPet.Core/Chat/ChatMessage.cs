namespace DeskPet.Core.Chat;

/// <summary>
/// 对话角色，对应 Swift <c>ChatMessage.Role</c>（user/assistant/system，顺序一致）。
/// </summary>
public enum ChatRole
{
    /// <summary>用户消息（Swift <c>.user</c>）。</summary>
    User,

    /// <summary>助手回复（Swift <c>.assistant</c>）。</summary>
    Assistant,

    /// <summary>系统消息（Swift <c>.system</c>，人设/上下文注入用）。</summary>
    System,
}

/// <summary>
/// 单条聊天消息，对应 Swift <c>ChatMessage</c>（Codable + Defaults.Serializable，
/// Core 侧由 System.Text.Json 序列化，可持久化）。
/// Swift <c>init</c> 的默认参数（id = UUID()、isStreaming = false）由调用方按需提供。
/// </summary>
public record ChatMessage(Guid Id, ChatRole Role, string Content, bool IsStreaming);
