using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskPet.Core.Chat;
using DeskPet.Core.Pet;
using DeskPet.Core.Services;

namespace DeskPet.Windows.ViewModels;

/// <summary>
/// 气泡视图模型（Task 5）：内容状态 + 自言自语调度 + 对话流式发送。
/// 对应 Swift <c>PetBubbleManager.swift</c> 的状态机（current/isInputActive/
/// isChatSessionActive/expiresAt/scheduleHide）与 <c>PetChatManager.send</c> 的调用方语义。
/// 显示层截断（Ruling 9）：所有展示文本经 <see cref="ISettingsService.BubbleMaxLength"/> 截断。
/// 窗口层（BubbleWindow）通过事件订阅显示/隐藏，并驱动定时器与动作订阅。
/// </summary>
public partial class BubbleViewModel : ObservableObject
{
    private const double ActionSpeechDuration = 8;        // Swift actionSpeechDuration（128）
    private const double ChatReplyShowSeconds = 25;       // Swift chatMessagesChanged 最终回复展示 25s（415）
    private const double ErrorShowSeconds = 12;           // Swift 流式错误展示 12s（248）
    private const double NotConfiguredShowSeconds = 8;    // Swift 未配置提示 8s（311）
    private const double InitialSpeechDelaySeconds = 20;  // Swift startSpeechTimer 首句延迟 20s（344）

    /// <summary>做动作时开口的动作集合（Swift speakableActions 132）。</summary>
    private static readonly IReadOnlySet<PetAction> SpeakableActions = new HashSet<PetAction>
    {
        PetAction.Walk, PetAction.Sleep, PetAction.Yawn, PetAction.Happy,
        PetAction.Hurt, PetAction.Fall, PetAction.Drag, PetAction.Music,
    };

    private readonly PetManager _pet;
    private readonly ISettingsService _settings;
    private readonly IChatClient _chat;
    private readonly ChatHistoryStore _historyStore;
    private readonly Random _rng;

    private readonly List<ChatMessage> _history = new();
    private DateTime _nextSpeechAt;
    private DateTime? _hideAfter;
    private DateTime _lastActionSpeechAt = DateTime.MinValue;
    private bool _chatSessionActive;
    private bool _streamActive;
    private string _streamingText = "";
    private CancellationTokenSource? _streamCts;

    [ObservableProperty]
    private string? _userText;

    [ObservableProperty]
    private string? _petText;

    [ObservableProperty]
    private bool _isInputActive;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string _inputText = "";

    /// <summary>气泡内容变化 → 窗口显示（Swift showWindow）。</summary>
    public event EventHandler? ShowRequested;

    /// <summary>气泡内容清空 → 窗口隐藏（Swift hideWindow）。</summary>
    public event EventHandler? HideRequested;

    public BubbleViewModel(
        PetManager pet, ISettingsService settings, IChatClient chat, ChatHistoryStore historyStore, Random? rng = null)
    {
        _pet = pet ?? throw new ArgumentNullException(nameof(pet));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _rng = rng ?? Random.Shared;
        _nextSpeechAt = DateTime.UtcNow.AddSeconds(InitialSpeechDelaySeconds);
        _history.AddRange(_historyStore.Load());
    }

    /// <summary>是否有待展示内容（Swift current != nil；拖拽/隐藏后恢复显示的依据）。</summary>
    public bool HasContent => UserText != null || PetText != null;

    /// <summary>对话是否已配置（Swift <c>isConfigured</c>：开关开启 + 已填模型名）。</summary>
    public bool IsChatConfigured => _settings.ChatEnabled && !string.IsNullOrWhiteSpace(_settings.ChatModel);

    /// <summary>内存中的会话历史（含本次对话，已持久化同步；仅窗口层读取，Swift messages）。</summary>
    public IReadOnlyList<ChatMessage> History => _history;

    // MARK: - 展示（Swift current 赋值语义）

    /// <summary>自言自语台词（Swift showPhrase 367-379）：small 档，8 秒后自动消失。</summary>
    public void ShowPhrase(string phrase, DateTime now)
    {
        if (_pet.IsDragging) return;   // 拖拽中不打扰（Swift 拖拽隐藏语义的兜底）
        UserText = null;
        PetText = Truncate(phrase);
        IsStreaming = false;
        _hideAfter = now.AddSeconds(ActionSpeechDuration);
        ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>打开输入框（Swift openInput 263-277）：提示语 + 保持显示。流式进行中忽略（防重入）。</summary>
    public void OpenInput()
    {
        if (IsStreaming) return;   // 流式进行中不打开输入：避免输入框抢占流式中的气泡
        _hideAfter = null;
        _chatSessionActive = true;
        UserText = null;
        PetText = Truncate(IsChatConfigured ? "想聊点什么？" : "还没配置 AI，去 设置 → Chat 填一下就能聊天啦");
        IsStreaming = false;
        ShowRequested?.Invoke(this, EventArgs.Empty);
        IsInputActive = true;
    }

    /// <summary>关闭输入框（Swift closeInput 279-285）：保留当前内容，8 秒后消失。</summary>
    public void CloseInput()
    {
        if (!IsInputActive) return;
        IsInputActive = false;
        _chatSessionActive = false;
        if (HasContent) _hideAfter = DateTime.UtcNow.AddSeconds(ActionSpeechDuration);
    }

    /// <summary>清空并隐藏（Swift dismissBubble 287-293；回窝/显式关闭时调用）。</summary>
    public void Dismiss()
    {
        _streamCts?.Cancel();
        _streamCts = null;
        _streamActive = false;      // 迟到的流式增量全部丢弃，防止旧流文本复活（评审发现 2b）
        _streamingText = "";
        _hideAfter = null;
        IsInputActive = false;
        IsStreaming = false;
        _chatSessionActive = false;
        UserText = null;
        PetText = null;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    // MARK: - 对话发送（Swift send 296-325 + PetChatManager.send 87-184）

    /// <summary>
    /// 发送当前输入：追加 user + 空 assistant 占位（本地状态机用）→ StreamChatAsync
    /// （快照剔除流式占位：占位不得到达客户端，否则 BuildMessages 会重复追加 user 输入
    /// 且空 assistant 回合进入请求体——评审发现 1）→ 定稿/报错 → 持久化（cap = ChatHistoryCount）。
    /// 未配置时显示 Swift 风格提示（PetBubbleManager.swift 300-313）而不调用客户端。
    /// 流式进行中禁止重入（评审发现 2a：重入会取消旧流并污染 _streamActive 状态）。
    /// </summary>
    public async Task SendAsync()
    {
        if (IsStreaming) return;   // 重入防护：新发送不得打断进行中的流
        var trimmed = InputText.Trim();
        if (trimmed.Length == 0) return;
        InputText = "";
        IsInputActive = false;

        if (!IsChatConfigured)
        {
            // Swift 300-313：发送前已置 isChatSessionActive=true（提示期间自动台词不得覆盖），
            // 8 秒后气泡过期清除。
            _chatSessionActive = true;
            UserText = Truncate(trimmed);
            PetText = Truncate("还没配置 AI 连接，去 设置 → Chat 填 Base URL / API Key 吧");
            IsStreaming = false;
            _hideAfter = DateTime.UtcNow.AddSeconds(NotConfiguredShowSeconds);
            ShowRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        UserText = Truncate(trimmed);
        PetText = "…";
        IsStreaming = true;
        _hideAfter = null;
        _chatSessionActive = true;
        ShowRequested?.Invoke(this, EventArgs.Empty);

        _history.Add(new ChatMessage(Guid.NewGuid(), ChatRole.User, trimmed, IsStreaming: false));
        var assistantId = Guid.NewGuid();
        _history.Add(new ChatMessage(assistantId, ChatRole.Assistant, "", IsStreaming: true));

        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        var cts = _streamCts;
        _streamActive = true;
        _streamingText = "";
        // Progress 捕获创建时（UI 线程）的 SynchronizationContext → 增量回调回到 UI 线程。
        var progress = new Progress<string>(piece =>
        {
            if (!_streamActive) return;   // 定稿后的迟到增量丢弃（竞态兜底）
            _streamingText += piece;
            PetText = Truncate(_streamingText);
        });

        string reply;
        try
        {
            // 快照 = 已定稿历史 + 本次 user 消息；流式占位只属于本地状态机（IsStreaming=true 一律剔除）。
            var payload = _history.Where(m => !m.IsStreaming).ToList();
            reply = await _chat.StreamChatAsync(payload, trimmed, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _streamActive = false;
            _history.RemoveAll(m => m.Id == assistantId);   // 会话被打断：不落历史（与错误路径一致）
            return;
        }
        catch (Exception ex)
        {
            _streamActive = false;
            _history.RemoveAll(m => m.Id == assistantId);   // 失败移除空占位（Swift 177-179）
            UserText = Truncate(trimmed);
            PetText = Truncate(ex is PetChatException ? ex.Message : "出错了，再等等我吧～");
            IsStreaming = false;
            _hideAfter = DateTime.UtcNow.AddSeconds(ErrorShowSeconds);
            Persist();
            ShowRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _streamActive = false;
        _history[_history.Count - 1] = new ChatMessage(assistantId, ChatRole.Assistant, reply, IsStreaming: false);
        UserText = Truncate(trimmed);
        PetText = Truncate(reply.Length == 0 ? "（空回复）" : reply);
        IsStreaming = false;
        _hideAfter = DateTime.UtcNow.AddSeconds(ChatReplyShowSeconds);
        Persist();
        ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    // MARK: - 定时自言自语（Swift speechTick 347-365）

    /// <summary>每秒定时心跳：过期气泡消失 + 到点按当前动作随机说一句（±25% 抖动间隔）。</summary>
    public void OnSpeechTick(DateTime now)
    {
        if (_hideAfter is { } hideAt && now >= hideAt)
        {
            _hideAfter = null;
            if (!IsInputActive)
            {
                UserText = null;
                PetText = null;
                IsStreaming = false;
                _chatSessionActive = false;
                HideRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        // 正在输入 / 正在聊天 / 正在流式时不自动说话（Swift 349）；回窝时不说话（350）
        if (IsInputActive || _chatSessionActive || IsStreaming) return;
        if (_pet.LifeState != PetLifeState.Roaming) return;
        if (now < _nextSpeechAt) return;

        _nextSpeechAt = now.Add(NextSpeechInterval());
        if (BubblePhrases.Pick(_pet.Action, _rng) is { } phrase)
        {
            ShowPhrase(phrase, now);
        }
    }

    /// <summary>动作切换触发说话（Swift actionChanged 382-391），带冷却防刷屏。</summary>
    public void HandleActionChanged(PetAction action, DateTime now)
    {
        if (!SpeakableActions.Contains(action)) return;
        if (_chatSessionActive || IsInputActive || IsStreaming) return;
        if (_pet.LifeState != PetLifeState.Roaming) return;
        var cooldown = Math.Max(20, _settings.BubbleIntervalSeconds * 0.7);   // Swift actionSpeechCooldown 124-126
        if ((now - _lastActionSpeechAt).TotalSeconds < cooldown) return;
        _lastActionSpeechAt = now;
        if (BubblePhrases.Pick(action, _rng) is { } phrase)
        {
            ShowPhrase(phrase, now);
        }
    }

    /// <summary>下一次说话间隔：base = max(15, BubbleIntervalSeconds)，±25% 抖动（Swift 118-121）。</summary>
    private TimeSpan NextSpeechInterval()
    {
        var baseSeconds = Math.Max(15, _settings.BubbleIntervalSeconds);
        var low = baseSeconds * 0.75;
        var high = baseSeconds * 1.5;
        return TimeSpan.FromSeconds(low + _rng.NextDouble() * (high - low));
    }

    // MARK: - 持久化（Ruling 5：cap = ChatHistoryCount）

    private void Persist()
    {
        var trimmed = ChatHistoryStore.Trim(_history, _settings.ChatHistoryCount);
        _history.Clear();
        _history.AddRange(trimmed);
        _historyStore.Save(trimmed);
    }

    /// <summary>显示层截断（Ruling 9）：超过 BubbleMaxLength 的展示文本截断（历史仍存全文）。</summary>
    private string? Truncate(string? text)
    {
        if (text is null) return null;
        var max = Math.Max(1, _settings.BubbleMaxLength);
        return text.Length <= max ? text : text[..max];
    }
}
