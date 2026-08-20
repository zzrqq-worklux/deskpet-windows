using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeskPet.Core.Services;

namespace DeskPet.Core.Pet;

/// <summary>
/// 宠物运动模式（Swift PetMovementMode 的最小形式；climbing/hanging 等遗留模式直接回落 Floor，Swift 324-329）。
/// </summary>
internal enum PetMovementMode { Floor, Falling }

/// <summary>
/// 桌面宠物状态中枢（Swift <c>PetManager.swift</c> 移植，T4 范围：走动/掉落物理 + 行为 AI + 属性衰减）。
/// 回窝走回/淡出流程由 T5 补齐。
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

    // MARK: - 测试观察点（T4 行为层内部状态）

    /// <summary>当前走动目标（测试用只读观察）。</summary>
    internal (double X, double Y)? WalkTarget => _walkTarget;

    /// <summary>当前运动模式（测试用只读观察）。</summary>
    internal PetMovementMode MovementMode => _movementMode;

    // MARK: - 内部状态

    private readonly PetAnimationLoop _loop = new();
    private bool _wasMusicPlaying;
    /// <summary>睡觉编排标志（T4 startSleeping 置位；哈欠播完后进入睡眠）。</summary>
    private bool _pendingSleep;
    /// <summary>睡眠维持剩余秒数（哈欠播完置 5..15，T3 已有）。</summary>
    private double _sleepHoldRemaining;
    /// <summary>待机剩余秒数（行为 AI 递减；归零触发 decideNextBehavior；EndDrag 重置）。</summary>
    private double _idleCounter = 4;
    private PetMovementMode _movementMode = PetMovementMode.Floor;
    /// <summary>生命周期标记（Swift start/stop 的计时器由窗口层持有，T6；Core 不建计时器）。</summary>
    private bool _running;

    // MARK: - T4 走动物理 / 掉落物理 / 属性衰减内部状态

    /// <summary>当前走动目标（nil=不在走动；Swift walkTarget）。</summary>
    private (double X, double Y)? _walkTarget;
    /// <summary>掉落当前速度（pt/s，向下为正；Swift fallVelocity）。</summary>
    private double _fallVelocity;
    /// <summary>距下次属性衰减的累计秒数（Swift statTimer 60s 间隔在 Tick 内累计）。</summary>
    private double _statElapsed;
    /// <summary>距下次诊断日志的累计秒数（Swift tickCount % 300）。</summary>
    private double _diagnosticsElapsed;

    private const double WalkSpeed = 70;          // Swift walkSpeed
    private const double Gravity = 500;           // Swift gravity（匹配 fall 动画时长）
    private const double StatIntervalSeconds = 60;
    private const double DiagnosticsIntervalSeconds = 5;

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

    // MARK: - 每帧推进（Swift animTick 259-447）

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

        // 2. 属性衰减（Swift statTimer 749-754：每 60s 一次；Tick 内累计）
        _statElapsed += dt;
        if (_statElapsed >= StatIntervalSeconds)
        {
            _statElapsed -= StatIntervalSeconds;
            StatTick();
        }

        // 3. 移动：走路 / 掉落（Swift 286-329）
        switch (_movementMode)
        {
            case PetMovementMode.Floor:
                if (_walkTarget is { } target && !IsDragging && !_wasMusicPlaying
                    && LifeState == PetLifeState.Roaming)
                {
                    var position = WindowPosition ?? InitialRoamingPosition();
                    var dx = target.X - position.X;
                    var dy = target.Y - position.Y;
                    var distance = Math.Sqrt(dx * dx + dy * dy);
                    var step = WalkSpeed * dt;
                    if (distance <= step)
                    {
                        // 到点 → Idle + 待机计时重置（Swift 295-304）
                        WindowPosition = target;
                        _walkTarget = null;
                        SetAction(PetAction.Idle);
                        ResetIdleCounter();
                    }
                    else
                    {
                        position.X += dx / distance * step;
                        position.Y += dy / distance * step;
                        WindowPosition = position;
                    }
                }
                break;
            case PetMovementMode.Falling:
                // 重力自由落体（被打断掉落 / 出窝从刘海掉下来，Swift 311-323）
                _fallVelocity += Gravity * dt;
                var pos = WindowPosition ?? InitialRoamingPosition();
                pos.Y -= _fallVelocity * dt;
                var floorY = CurrentScreen().Y + WindowSize.Height * 0.06;
                if (pos.Y <= floorY)
                {
                    pos.Y = floorY;
                    WindowPosition = pos;
                    Land();
                }
                else
                {
                    WindowPosition = pos;
                }
                break;
            default:
                // 遗留模式（approachingEdge/climbing/hanging 等，Swift 324-329）：安全回落地面状态
                _movementMode = PetMovementMode.Floor;
                SetAction(PetAction.Idle);
                ResetIdleCounter();
                break;
        }

        // 4. 帧推进（Swift 359-432）
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
        // 兜底动作（无真实素材 → TotalFrames=0）：Swift total=0 → total-1=-1 → 首帧即算播完，此处等效补上
        if (!_loop.IsLoop && (_loop.OneShotFinished || (_loop.TotalFrames == 0 && CurrentFrameIndex != null)))
        {
            if (Action == PetAction.Yawn && _pendingSleep)
            {
                // 打哈欠 → 睡觉（startSleeping 置位后激活）
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

        // 5. 行为 AI（Swift 434-440）：仅在站立待机时决策，走动/睡眠/一次性动画期间不打断
        if (LifeState == PetLifeState.Roaming && !IsDragging && !_wasMusicPlaying
            && _movementMode == PetMovementMode.Floor && Action == PetAction.Idle)
        {
            _idleCounter -= dt;
            if (_idleCounter <= 0) DecideNextBehavior();
        }

        // 6. 状态诊断日志（Swift 442-447，每 5s 一条）
        _diagnosticsElapsed += dt;
        if (_diagnosticsElapsed >= DiagnosticsIntervalSeconds)
        {
            _diagnosticsElapsed = 0;
            Debug.WriteLine(
                $"[Pet] action={Action} lifeState={LifeState} musicPlaying={_wasMusicPlaying} " +
                $"dragging={IsDragging} floor={_movementMode == PetMovementMode.Floor} alpha={WindowAlpha:F2} " +
                $"walkTarget={(_walkTarget != null ? "set" : "nil")} idleCounter={_idleCounter:F1}");
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

    /// <summary>拎起（Swift 721-731）：打断走动，目标清除。movingHome/fading 复位属 T5 字段。</summary>
    public void StartDrag()
    {
        if (LifeState != PetLifeState.Roaming) return;
        IsDragging = true;
        _walkTarget = null;
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

    // MARK: - 行为 AI（Swift 452-605）

    /// <summary>待机决策：心情差时更容易打瞌睡/闹脾气；心情好时丰富的随机行为（Swift 452-496）。</summary>
    private void DecideNextBehavior()
    {
        var sad = Happiness < 25;
        var roll = _rng.NextDouble();

        if (sad)
        {
            if (roll < 0.30)
            {
                StartSleeping();
            }
            else if (roll < 0.50)
            {
                SetAction(PetAction.Hurt);
            }
            else if (roll < 0.70)
            {
                StartWalking();
            }
            else
            {
                ResetIdleCounter();
            }
        }
        else
        {
            // 0.20 走路 / 0.12 睡觉（打哈欠过渡）/ 0.10 一次性小动作 / 0.58 继续待机（换 idle 变体）
            if (roll < 0.20)
            {
                StartWalking();
            }
            else if (roll < 0.32)
            {
                StartSleeping();
            }
            else if (roll < 0.42)
            {
                var mini = _rng.NextDouble();
                if (mini < 0.50)
                {
                    SetAction(PetAction.Yawn);          // 单独打哈欠（不入睡）
                }
                else if (mini < 0.80)
                {
                    SetAction(PetAction.Happy);         // 突然开心一下
                }
                else
                {
                    SetAction(PetAction.Hurt);          // 突然一惊
                }
                ResetIdleCounter();
            }
            else
            {
                ResetIdleCounter();
                // 继续待机：强制换一个 idle 变体（避免一直同一个姿势）
                if (_skin.VariantCount(PetAction.Idle) > 1)
                {
                    SetAction(PetAction.Idle, forceReshuffle: true);
                }
            }
        }
    }

    /// <summary>开始走动：选目标 + 朝向 + 切 Walk（Swift 498-506）。目标为空（屏太小）则重置待机。</summary>
    private void StartWalking()
    {
        var target = RandomWalkTarget();
        if (target == null)
        {
            ResetIdleCounter();
            return;
        }
        _walkTarget = target;
        UpdateFacing(target.Value);
        SetAction(PetAction.Walk);
    }

    /// <summary>睡觉：先打哈欠，哈欠播完自动进入睡眠（避免"站 → 睡"直接跳变；Swift 509-514）。</summary>
    private void StartSleeping()
    {
        _walkTarget = null;
        _pendingSleep = true;
        SetAction(PetAction.Yawn);
        _idleCounter = 6 + _rng.NextDouble() * 8;       // Swift 6...14
    }

    /// <summary>在当前屏内随机选一个走动目标：避开顶部 6% 与底部 10% 区，
    /// 目标至少距当前 20% 屏宽（10 次尝试后返回最远候选；Swift 518-549）。</summary>
    internal (double X, double Y)? RandomWalkTarget()
    {
        var screen = CurrentScreen();
        var size = WindowSize;
        var minX = screen.X + screen.Width * 0.06;
        var maxX = screen.X + screen.Width * 0.94 - size.Width;
        var minY = screen.Y + screen.Height * 0.10;
        var maxY = screen.Y + screen.Height * 0.60;
        if (maxX <= minX || maxY <= minY) return null;

        var current = WindowPosition ?? (screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
        var minDistance = screen.Width * 0.20;

        var best = (maxX, maxY);
        var bestDistance = 0.0;
        for (var i = 0; i < 10; i++)
        {
            var candidate = (X: minX + _rng.NextDouble() * (maxX - minX),
                             Y: minY + _rng.NextDouble() * (maxY - minY));
            var d = Math.Sqrt(Math.Pow(candidate.X - current.X, 2) + Math.Pow(candidate.Y - current.Y, 2));
            if (d > bestDistance)
            {
                best = candidate;
                bestDistance = d;
            }
            if (d >= minDistance) return candidate;
        }
        return best;                                    // 10 次都没够远（屏太小），用最远的候选
    }

    /// <summary>朝向目标（Swift 551-555）。</summary>
    private void UpdateFacing((double X, double Y) target)
    {
        if (_windowPosition is { } pos)
        {
            FacingLeft = target.X < pos.X;
        }
    }

    /// <summary>被打断 → 立刻掉下来（Swift 580-588；movingHome/fading 复位属 T5 字段，本任务无）。</summary>
    internal void InterruptToFall()
    {
        _walkTarget = null;
        _movementMode = PetMovementMode.Falling;
        _fallVelocity = 0;
        SetAction(PetAction.Fall);
    }

    /// <summary>落地：速度快则反弹 25% 再落一次；慢则停止物理运动但保持 Fall 动画播完（Swift 593-605）。</summary>
    private void Land()
    {
        if (Math.Abs(_fallVelocity) > 300)
        {
            // 弹跳：反弹 25% 速度，再落一次（衰减更狠，避免低重力下多次弹跳拖长）
            _fallVelocity = -_fallVelocity * 0.25;
        }
        else
        {
            _fallVelocity = 0;
            _movementMode = PetMovementMode.Floor;
            LifeState = PetLifeState.Roaming;
            // 不切 idle！保持 action = Fall，让 fall 动画播完剩余帧后自然过渡到 idle
        }
    }

    /// <summary>当前所在屏（含窗口位置的屏优先，否则主屏；Swift currentScreen 558-565）。</summary>
    private Rect CurrentScreen()
    {
        if (_windowPosition is { } pos)
        {
            foreach (var wa in _screens.WorkAreas)
            {
                if (pos.X >= wa.X && pos.X < wa.X + wa.Width
                    && pos.Y >= wa.Y && pos.Y < wa.Y + wa.Height)
                {
                    return wa;
                }
            }
        }
        return _screens.PrimaryWorkArea;
    }

    /// <summary>无位置时的初始漫游点（Swift 567-575）。</summary>
    private (double X, double Y) InitialRoamingPosition()
    {
        var screen = CurrentScreen();
        var size = WindowSize;
        return (screen.X + screen.Width / 2 - size.Width / 2, screen.Y + screen.Height * 0.22);
    }

    /// <summary>窗口尺寸 = 皮肤基础帧尺寸 × 用户缩放 × 皮肤 scale（Swift windowSize 77-84；
    /// minimumWindowHeight 下限不适用：窗口实际尺寸由 T6 窗口层管理）。</summary>
    private (double Width, double Height) WindowSize
    {
        get
        {
            if (_skin.BaseFrameSize is { } baseSize)
            {
                var scale = _settings.PetScale * _skin.Scale;
                return (baseSize.Width * scale, baseSize.Height * scale);
            }
            return (120, 120);
        }
    }

    // MARK: - 属性衰减（Swift 749-754，60s 间隔在 Tick 内累计触发）

    private void StatTick()
    {
        var speed = _settings.StatDecaySpeed;
        Cleanliness = Math.Max(0, Cleanliness - 0.8 * speed);
        // 心情缓慢趋向 50（长时间不理会低落）
        Happiness = Math.Min(100, Math.Max(0, Happiness + (50 - Happiness) * 0.04));
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

    /// <summary>当前动作已缓存帧数（Swift frames(for:) 语义）。CachedCount 契约（T3 决策）：逐 tick 从
    /// skin 缓存读取，窗口层补齐（BackfillIfNeeded）后下一 tick 自动生效，无需窗口层回调更新。
    /// 变体级：目标变体空时用同动作首个非空变体顶替；动作级：动作无真实素材时回退到兜底动作的缓存帧数
    /// （Swift fallbackFrames 语义）——否则 HasMaterial 通过但 Cache[Action] 恒空，循环永不推进，
    /// 如 LeaveHome 的 Fall 在无 fall 素材皮肤上不会渲染 drag/idle 兜底帧。</summary>
    private int CachedFrameCount()
    {
        lock (_skin.SyncRoot)
        {
            var count = CachedVariantCount(Action, _currentVariant);
            if (count > 0) return count;
            // 真实素材加载中：过渡帧仅来自本动作变体（Swift frames(for:) transitional 语义），不落兜底动作
            if (_skin.HasRealMaterial(Action)) return 0;
            return FallbackFrameCount();
        }
    }

    private int CachedVariantCount(PetAction action, int variant)
    {
        if (!_skin.Cache.TryGetValue(action, out var variants)) return 0;
        if (variant >= 0 && variant < variants.Count && variants[variant].Count > 0) return variants[variant].Count;
        return variants.FirstOrDefault(v => v.Count > 0)?.Count ?? 0;
    }

    /// <summary>兜底动作的缓存帧数（首个非空变体；链与 PetSkin.HasMaterial/FallbackFramesAsync 同构，
    /// 对应 Swift SpriteAnimator.swift fallbackFrames(for:) 594-624）。</summary>
    private int FallbackFrameCount()
    {
        foreach (var fallback in FallbackActions(Action))
        {
            if (_skin.Cache.TryGetValue(fallback, out var variants))
            {
                var first = variants.FirstOrDefault(v => v.Count > 0);
                if (first != null) return first.Count;
            }
        }
        return 0;
    }

    private static IReadOnlyList<PetAction> FallbackActions(PetAction action) => action switch
    {
        PetAction.Music => new[] { PetAction.Idle },
        PetAction.Climb => new[] { PetAction.Walk, PetAction.Idle },
        PetAction.Hang or PetAction.Dizzy or PetAction.Yawn => new[] { PetAction.Idle },
        PetAction.Fall => new[] { PetAction.Drag, PetAction.Idle },
        PetAction.Home => new[] { PetAction.Sleep, PetAction.Idle },
        _ => Array.Empty<PetAction>()
    };

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
