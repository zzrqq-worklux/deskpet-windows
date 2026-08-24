namespace DeskPet.Core.Services;

/// <summary>
/// 持久化设置读写服务，对应 Swift 端 <c>Defaults[.petScale]</c> 等 UserDefaults 语义。
/// 属性名称与含义逐一对应 Swift 的 Defaults key：PetScale/RoamInterval/StatDecaySpeed/
/// SoundVolume(0.0-1.0)/MusicDance/SkinDirectory("" 表示默认皮肤目录)，
/// 以及聊天/RAG 配置：ChatEnabled/ChatBaseUrl/ChatApiKey/ChatModel/ChatSystemPrompt/
/// ChatHistoryCount/BubbleIntervalSeconds/BubbleMaxLength/EmbeddingModel/RagEnabled。
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// 宠物人设 system prompt 默认文案（对应 Swift <c>Defaults[.petChatSystemPrompt]</c> 的默认值，
    /// Constants.swift petChatSystemPrompt）。
    /// </summary>
    public static string DefaultChatSystemPrompt =>
        "你是一只住在 macOS 桌面上的可爱宠物猫，名字叫「小桌」。主人是你的好朋友。"
        + "请用简短、温暖、俏皮的口吻像朋友一样闲聊，回复尽量控制在 2-3 句话以内，"
        + "偶尔可以用一两个表情符号（😺🐾✨）。";

    /// <summary>宠物缩放比例（Swift <c>Defaults[.petScale]</c>，默认 1.0）。</summary>
    double PetScale { get; set; }

    /// <summary>自由漫游间隔秒数（Swift <c>Defaults[.petRoamInterval]</c>）。</summary>
    double RoamInterval { get; set; }

    /// <summary>属性值自然衰减速度（Swift <c>Defaults[.petStatDecaySpeed]</c>）。</summary>
    double StatDecaySpeed { get; set; }

    /// <summary>音效音量，取值 0.0-1.0（Swift <c>Defaults[.petSoundVolume]</c>）。</summary>
    double SoundVolume { get; set; }

    /// <summary>是否随音乐律动（Swift <c>Defaults[.petMusicDance]</c>）。</summary>
    bool MusicDance { get; set; }

    /// <summary>皮肤目录路径，空字符串表示使用默认目录（Swift <c>Defaults[.petSkinDirectory]</c>）。</summary>
    string SkinDirectory { get; set; }

    /// <summary>对话总开关（Swift <c>Defaults[.petChatEnabled]</c>，默认 false）。</summary>
    bool ChatEnabled { get; set; }

    /// <summary>OpenAI 兼容 API Base URL（Swift <c>Defaults[.petChatBaseURL]</c>，默认 ""，空时由调用方补全/报错）。</summary>
    string ChatBaseUrl { get; set; }

    /// <summary>API Key（Bearer Token；Swift <c>Defaults[.petChatAPIKey]</c>，默认 ""，本地服务可留空）。</summary>
    string ChatApiKey { get; set; }

    /// <summary>对话模型名（Swift <c>Defaults[.petChatModel]</c>，默认 ""）。</summary>
    string ChatModel { get; set; }

    /// <summary>宠物人设 system prompt（Swift <c>Defaults[.petChatSystemPrompt]</c>，默认见 <see cref="DefaultChatSystemPrompt"/>）。</summary>
    string ChatSystemPrompt { get; set; }

    /// <summary>发送给 LLM 的历史消息条数上限（Swift <c>Defaults[.petChatMaxHistory]</c>，默认 20）。</summary>
    int ChatHistoryCount { get; set; }

    /// <summary>气泡自言自语间隔秒数（Swift <c>Defaults[.petBubbleSpeechInterval]</c>，默认 120）。</summary>
    double BubbleIntervalSeconds { get; set; }

    /// <summary>气泡文本最大长度（计划新增，无直接 Swift Defaults key，默认 120）。</summary>
    int BubbleMaxLength { get; set; }

    /// <summary>Embedding 模型名（Swift <c>Defaults[.petRAGEmbeddingModel]</c>，默认 ""）。</summary>
    string EmbeddingModel { get; set; }

    /// <summary>对话注入 RAG 检索上下文开关（计划新增；Swift 端对应 <c>VectorStoreManager.isConfigured</c> 语义，默认 false）。</summary>
    bool RagEnabled { get; set; }
}
