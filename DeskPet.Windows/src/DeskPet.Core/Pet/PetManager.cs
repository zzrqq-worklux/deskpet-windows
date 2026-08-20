using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeskPet.Core.Services;

namespace DeskPet.Core.Pet;

/// <summary>
/// 宠物运动模式（Swift PetMovementMode 的最小形式；climbing/hanging 等遗留模式由 T4 补齐并直接回落 Floor）。
/// </summary>
internal enum PetMovementMode { Floor, Falling }

/// <summary>
/// 桌面宠物状态中枢（Swift <c>PetManager.swift</c> 移植，T3 范围：状态与动作切换）。
/// 行为 AI、走动物理、掉落、回窝流程由 T4/T5 补齐。
/// </summary>
public class PetManager : INotifyPropertyChanged
{
    // MARK: - 动画状态（窗口层订阅）

    private PetAction _action = PetAction.Idle;
    public PetAction Action { get => _action; internal set => SetField(ref _action, value); }

    private bool _facingLeft;
    public bool FacingLeft { get => _facingLeft; internal set => SetField(ref _facingLeft, value); }

    private int? _currentFrameIndex;
    /// <summary>当前帧索引（null=无帧：动作刚切换、素材缺失或加载中）。帧内容获取由窗口层负责（T6）。</summary>
    public int? CurrentFrameIndex { get => _currentFrameIndex; internal set => SetField(ref _currentFrameIndex, value); }

    private double _dizzyAngle;
    public double DizzyAngle { get => _dizzyAngle; internal set => SetField(ref _dizzyAngle, value); }

    // MARK: - 宠物属性（0-100）

    private double _happiness = 100;
    public double Happiness { get => _happiness; internal set => SetField(ref _happiness, value); }

    private double _cleanliness = 100;
    public double Cleanliness { get => _cleanliness; internal set => SetField(ref _cleanliness, value); }

    // MARK: - 生命周期

    private PetLifeState _lifeState = PetLifeState.Roaming;
    public PetLifeState LifeState { get => _lifeState; internal set => SetField(ref _lifeState, value); }

    private bool _isDragging;
    public bool IsDragging { get => _isDragging; internal set => SetField(ref _isDragging, value); }

    // MARK: - 窗口控制（窗口层订阅）

    private (double X, double Y)? _windowPosition;
    public (double X, double Y)? WindowPosition { get => _windowPosition; internal set => SetField(ref _windowPosition, value); }

    private double _windowAlpha = 1;
    public double WindowAlpha { get => _windowAlpha; internal set => SetField(ref _windowAlpha, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    // MARK: - 依赖

    private PetSkin _skin;
    private readonly ISettingsService _settings;
    private readonly IAudioService _audio;
    private readonly IMusicService _music;
    private readonly IScreenInfo _screens;   // T4/T5 物理与回窝目标计算使用（本任务未用）
    private readonly Random _rng;
    private readonly Func<Task<PetSkin?>>? _reloadSkin;

    /// <summary>当前动作的选定变体索引（SetAction 时选一次并固定，动画循环中不重抽；T6 用其调 BackfillIfNeeded）。</summary>
    internal int CurrentVariant => _currentVariant;
    private int _currentVariant;

    // MARK: - 内部状态

    private readonly PetAnimationLoop _loop = new();
    private bool _wasMusicPlaying;
    /// <summary>睡觉编排标志（T4 startSleeping 置位；本任务恒 false，分支惰性）。</summary>
    private bool _pendingSleep;
    /// <summary>睡眠维持剩余秒数（T4；本任务恒 0）。</summary>
    private double _sleepHoldRemaining;
    /// <summary>待机剩余秒数（T4 行为 AI 递减；EndDrag 重置）。</summary>
    private double _idleCounter = 4;
    private PetMovementMode _movementMode = PetMovementMode.Floor;
    /// <summary>生命周期标记（Swift start/stop 的计时器由窗口层持有，T6；Core 不建计时器）。</summary>
    private bool _running;

    public PetManager(
        PetSkin skin,
        ISettingsService settings,
        IAudioService audio,
        IMusicService music,
        IScreenInfo screens,
        Random? rng = null,
        Func<Task<PetSkin?>>? reloadSkin = null)
    {
        _skin = skin;
        _settings = settings;
        _audio = audio;
        _music = music;
        _screens = screens;
        _rng = rng ?? Random.Shared;
        _reloadSkin = reloadSkin;
        ConfigureLoop();
    }

    /// <summary>工厂：包装构造 + Start（皮肤加载由调用方完成，SkinDirectory 语义在 T6 接入）。</summary>
    public static PetManager Load(
        PetSkin skin,
        ISettingsService settings,
        IAudioService audio,
        IMusicService music,
        IScreenInfo screens,
        Random? rng = null)
    {
        var manager = new PetManager(skin, settings, audio, music, screens, rng);
        manager.Start();
        return manager;
    }

    // MARK: - 生命周期（Swift 216-238）

    public void Start()
    {
        // 生命周期标记：60fps 动画计时器由窗口层持有（T6），Core 的 Start/Stop 仅作标记。
        _running = true;
    }

    public void Stop()
    {
        _running = false;
    }

    // MARK: - 每帧推进（Swift animTick 259-432 的 T3 部分）

    public void Tick(double dt)
    {
        // 1. 音乐联动（Swift 262-278）：IMusicService.IsPlaying 恒 false（Phase 1 占位）→ 永不激活
        if (_settings.MusicDance && _skin.HasRealMaterial(PetAction.Music)
            && _music.IsPlaying && _movementMode == PetMovementMode.Floor)
        {
            if (!_wasMusicPlaying)
            {
                _wasMusicPlaying = true;
                SetAction(PetAction.Music);
            }
        }
        else if (_wasMusicPlaying)
        {
            _wasMusicPlaying = false;
            if (!IsDragging)
            {
                SetAction(PetAction.Idle);
                ResetIdleCounter();
            }
        }

        // 2. 帧推进（Swift animTick §4, 359-432；移动/淡出/AI 为 T4/T5）
        if (!_skin.HasMaterial(Action))
        {
            CurrentFrameIndex = null;   // 真正缺素材 → 无帧（占位渲染由窗口层负责）
            return;
        }
        _loop.CachedCount = CachedFrameCount();
        _loop.LoopPoint = _skin.LoopPoint(Action, _currentVariant);   // 签名加载完成后才可用，逐 tick 读取
        var changed = _loop.Advance(dt);
        if (changed) CurrentFrameIndex = _loop.CurrentIndex;
        // 流式补齐 skin.BackfillIfNeeded 由窗口层调用（T6，沿用 Phase 0 调用点）

        // 一次性动作播完 → 自然过渡（Swift 415-431）
        if (!_loop.IsLoop && _loop.OneShotFinished)
        {
            if (Action == PetAction.Yawn && _pendingSleep)
            {
                // 打哈欠 → 睡觉（T4 startSleeping 置位后激活）
                _pendingSleep = false;
                SetAction(PetAction.Sleep);
                _sleepHoldRemaining = 5 + _rng.NextDouble() * 10;   // Swift 5...15
            }
            else if (Action == PetAction.Sleep && _sleepHoldRemaining > 0)
            {
                // 睡眠保持：冻结末帧（loop 已钳制在 CachedCount-1，CurrentFrameIndex 不变）
                _sleepHoldRemaining -= dt;
            }
            else if (!IsDragging)
            {
                SetAction(PetAction.Idle);
                ResetIdleCounter();
            }
        }
    }

    // MARK: - 交互 API（Swift 703-745）

    /// <summary>单击：戳一下。心情差会委屈（hurt），否则开心地蹦一下并减 1 心情（掉落/出窝中不响应）。</summary>
    public void Poke()
    {
        if (LifeState != PetLifeState.Roaming || IsDragging || _movementMode != PetMovementMode.Floor) return;
        if (Happiness < 30)
        {
            SetAction(PetAction.Hurt);
        }
        else
        {
            SetAction(PetAction.Happy);
            Happiness = Math.Max(0, Happiness - 1);
        }
    }

    /// <summary>抚摸（Swift 714-718）。</summary>
    public void Pet()
    {
        if (LifeState != PetLifeState.Roaming || IsDragging) return;
        Happiness = Math.Min(100, Happiness + 12);
        SetAction(PetAction.Happy);
    }

    /// <summary>拎起（Swift 721-731）。回窝字段清理（walkTarget/movingHome/fading）属 T4/T5 字段，本任务无。</summary>
    public void StartDrag()
    {
        if (LifeState != PetLifeState.Roaming) return;
        IsDragging = true;
        _movementMode = PetMovementMode.Floor;
        DizzyAngle = 0;
        SetAction(PetAction.Drag);
    }

    /// <summary>放下（Swift 734-738）。</summary>
    public void EndDrag()
    {
        IsDragging = false;
        SetAction(PetAction.Idle);
        ResetIdleCounter();
    }

    /// <summary>拖拽结束后把内部位置与窗口实际位置对齐（Swift 741-745）。</summary>
    public void SyncWindowPosition((double X, double Y)? pos)
    {
        if (pos.HasValue) WindowPosition = pos.Value;
    }

    // MARK: - 回窝 / 出窝（Swift 610-693 的 T3 骨架；物理走回/掉落/淡出在 T5）

    /// <summary>走回屏幕顶部中央 → 淡出 → AtHome（Swift 610-635）。本任务仅 guard；走回+淡出在 T5。</summary>
    public void GoHome()
    {
        // guard 与 Swift 一致（movingHome 为 T5 字段，尚不存在）
        if (LifeState != PetLifeState.Roaming || IsDragging) return;
        // T5：选目标屏 + 置 movingHome/walkTarget + SetAction(Walk) + 到位淡出 + AtHome + TrimToHomeOnly
    }

    /// <summary>瞬间进窝（Swift 639-662）：拖拽投放场景跳过走回。本任务仅 guard；淡出/AtHome 在 T5。</summary>
    public void SnapHome()
    {
        if (LifeState != PetLifeState.Roaming) return;
        // T5：定位目标点 + 淡出 + AtHome + TrimToHomeOnly
    }

    /// <summary>出窝（Swift 667-693）：状态翻转（Roaming + alpha=1 + Fall）；掉落物理与 ExitHomeOnlyMode 在 T5。</summary>
    public void LeaveHome()
    {
        if (LifeState != PetLifeState.AtHome) return;
        WindowAlpha = 1;
        LifeState = PetLifeState.Roaming;
        _movementMode = PetMovementMode.Falling;
        SetAction(PetAction.Fall);
        // T5：ExitHomeOnlyMode + 重力掉落物理 + 落地
    }

    // MARK: - 皮肤

    /// <summary>重新扫描素材目录（设置页手动触发；Swift 241-249）。skin 重载来源由调用方经 ctor reloadSkin 注入（T6）。</summary>
    public void ReloadSkin()
    {
        if (_reloadSkin != null)
        {
            try
            {
                var loaded = _reloadSkin().GetAwaiter().GetResult();
                if (loaded != null) _skin = loaded;
            }
            catch
            {
                // 加载失败：保留当前皮肤
            }
        }
        SetAction(PetAction.Idle, forceReshuffle: true);
    }

    // MARK: - 动作切换（Swift 761-802）

    /// <summary>切换动作：选变体、播音效、预取、循环复位并按动作配置（forceReshuffle=true 时同动作也重新进入）。</summary>
    internal void SetAction(PetAction newAction, bool forceReshuffle = false)
    {
        if (!forceReshuffle && Action == newAction) return;
        // 非哈欠/睡眠动作打断时，清除睡觉编排状态（否则下次哈欠会误入睡眠）
        if (newAction != PetAction.Yawn && newAction != PetAction.Sleep)
        {
            _pendingSleep = false;
            _sleepHoldRemaining = 0;
        }
        Action = newAction;
        _skin.MarkPlayed(newAction);
        // 进入动作时选一个变体并固定（优先已缓存变体，全部未缓存才随机）
        _currentVariant = _skin.PreferredVariant(newAction) ?? 0;
        // 音效策略（Swift 782-786）：变体重入/循环待机动作（idle/home）不重播音效，其余动作播一次
        if (forceReshuffle || newAction == PetAction.Idle || newAction == PetAction.Home)
        {
            _audio.Stop();
        }
        else
        {
            _audio.Play(_skin.Sound(newAction), _settings.SoundVolume);
        }
        // 预取下一个很可能用到的动作（Swift 788-800）
        switch (newAction)
        {
            case PetAction.Idle when LifeState == PetLifeState.Roaming:
                _skin.Prefetch(new[]
                {
                    PetAction.Idle, PetAction.Walk, PetAction.Yawn,
                    PetAction.Drag, PetAction.Happy, PetAction.Hurt, PetAction.Fall
                });
                break;
            case PetAction.Yawn when _pendingSleep:
                _skin.Prefetch(new[] { PetAction.Sleep });
                break;
        }
        // 动作切换：循环复位 + 按动作配置（Fps/IsLoop/TotalFrames；CachedCount/LoopPoint 每 tick 更新）
        ConfigureLoop();
        // 新动作尚未播放任何帧 → 发布"无帧"
        CurrentFrameIndex = null;
    }

    private void ConfigureLoop()
    {
        _loop.Reset();
        _loop.Fps = _skin.Fps(Action);
        _loop.IsLoop = _skin.Loops(Action) < 0;
        _loop.TotalFrames = _skin.TotalFrameCount(Action, _currentVariant);
        _loop.LoopPoint = _skin.LoopPoint(Action, _currentVariant);
    }

    /// <summary>当前动作已缓存帧数（Swift frames(for:) 语义：目标变体空时用同动作首个非空变体顶替）。
    /// CachedCount 契约（T3 决策）：逐 tick 从 skin 缓存读取，窗口层补齐（BackfillIfNeeded）后下一 tick 自动生效，
    /// 无需窗口层回调更新。</summary>
    private int CachedFrameCount()
    {
        lock (_skin.SyncRoot)
        {
            if (!_skin.Cache.TryGetValue(Action, out var variants)) return 0;
            if (_currentVariant >= 0 && _currentVariant < variants.Count && variants[_currentVariant].Count > 0)
                return variants[_currentVariant].Count;
            return variants.FirstOrDefault(v => v.Count > 0)?.Count ?? 0;
        }
    }

    /// <summary>随机重置待机计时（Swift 252-255；T4 行为 AI 递减使用）。</summary>
    private void ResetIdleCounter()
    {
        var baseValue = Math.Max(1.5, _settings.RoamInterval);
        _idleCounter = baseValue * 0.6 + _rng.NextDouble() * (baseValue * 0.4);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
