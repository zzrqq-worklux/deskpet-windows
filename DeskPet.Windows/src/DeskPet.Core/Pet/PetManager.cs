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
/// 桌面宠物状态中枢（Swift <c>PetManager.swift</c> 移植，T5 范围：回窝/出窝流程 + 拖拽投放托盘重设计）。
/// 走回最近屏幕边缘（左/右/上）→ 淡出 → AtHome（节能 trim）；出窝从顶部上方掉落回桌面（ExitHomeOnlyMode）。
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

    /// <summary>生命周期状态。AtHome=回窝节能（仅 home/fall 素材可加载）。</summary>
    public PetLifeState LifeState
    {
        get => _lifeState;
        internal set
        {
            if (SetField(ref _lifeState, value)) LifeStateChanged?.Invoke(value);
        }
    }

    /// <summary>生命周期切换事件（Swift @Published lifeState 对应；T8 托盘图标消费）。</summary>
    public event Action<PetLifeState>? LifeStateChanged;

    private bool _isDragging;
    public bool IsDragging { get => _isDragging; internal set => SetField(ref _isDragging, value); }

    // MARK: - 窗口控制（窗口层订阅）

    private (double X, double Y)? _windowPosition;
    public (double X, double Y)? WindowPosition { get => _windowPosition; internal set => SetField(ref _windowPosition, value); }

    private double _windowAlpha = 1;
    public double WindowAlpha { get => _windowAlpha; internal set => SetField(ref _windowAlpha, value); }

    // MARK: - 拖拽投放（Windows 托盘重设计：主屏工作区顶部中央投放区）

    private bool _isHoveringDropZone;
    /// <summary>当前是否悬停在投放区（拖拽中，由窗口层每帧调用 EvaluateNotchDropHover 更新；Swift isHoveringNotchDropZone）。</summary>
    public bool IsHoveringDropZone { get => _isHoveringDropZone; internal set => SetField(ref _isHoveringDropZone, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    // MARK: - 依赖

    private PetSkin _skin;
    private readonly ISettingsService _settings;
    private readonly IAudioService _audio;
    private readonly IMusicService _music;
    private readonly IScreenInfo _screens;   // T4/T5 物理与回窝目标/投放区计算使用
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

    /// <summary>回窝中（走回/淡出阶段；测试用只读观察，Swift movingHome）。</summary>
    internal bool MovingHome => _movingHome;

    /// <summary>淡出/淡入进行中（测试用只读观察，Swift fading）。</summary>
    internal bool Fading => _fading;

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
    /// <summary>回窝中（走回/淡出阶段；Swift movingHome，isHomeTransitioning 对应）。</summary>
    private bool _movingHome;
    /// <summary>淡出（回窝）/淡入（被打断的淡出恢复）进行中（Swift fading）。</summary>
    private bool _fading;

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
    private const double HomeFadeRate = 3.6;      // 淡出/淡入 ±0.06 / 60fps-tick（Swift 331-357，折算每秒）

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
    }

    public void Stop()
    {
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
        // 兜底：回窝途中目标丢失（被任何路径打断）→ 复位回窝状态，避免 movingHome 永久卡死（Swift 281-285）
        if (_movingHome && _walkTarget == null && !_fading)
        {
            _movingHome = false;
        }
        switch (_movementMode)
        {
            case PetMovementMode.Floor:
                if (_walkTarget is { } target && !IsDragging && !_wasMusicPlaying
                    && (LifeState == PetLifeState.Roaming || _movingHome))
                {
                    var position = WindowPosition ?? InitialRoamingPosition();
                    var dx = target.X - position.X;
                    var dy = target.Y - position.Y;
                    var distance = Math.Sqrt(dx * dx + dy * dy);
                    var step = WalkSpeed * dt;
                    if (distance <= step)
                    {
                        // 到点：回窝中 → 走回完成，开始淡出；否则 Idle + 待机计时重置（Swift 295-304）
                        WindowPosition = target;
                        _walkTarget = null;
                        if (_movingHome)
                        {
                            BeginHomeFadeout();
                        }
                        else
                        {
                            SetAction(PetAction.Idle);
                            ResetIdleCounter();
                        }
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

        // 3.5 透明度渐变（回窝淡出 / 淡出被中断后淡回，Swift 331-357）
        if (_fading)
        {
            var next = WindowAlpha + (_movingHome ? -HomeFadeRate : HomeFadeRate) * dt;
            if (_movingHome && next <= 0)
            {
                WindowAlpha = 0;
                _fading = false;
                LifeState = PetLifeState.AtHome;
                _movingHome = false;
                // 回窝后切 .home（睡眠姿势）而非 .idle：idle 逐帧请求会绕过 homeOnlyMode 拦截，
                // 把刚 trim 清掉的 idle 又加载回来；.home 只有 1 帧且 homeOnlyMode 允许（Swift 340-344）。
                SetAction(PetAction.Home);
                // 回窝节能：卸载所有活动动作帧，只保留 .home + .fall（出窝动画）
                _skin.TrimToHomeOnly();
            }
            else if (!_movingHome && next >= 1)
            {
                WindowAlpha = 1;
                _fading = false;
                LifeState = PetLifeState.Roaming;
                SetAction(PetAction.Idle);
                ResetIdleCounter();
            }
            else
            {
                WindowAlpha = next;
            }
        }

        // 4. 帧推进（Swift 359-432）
        if (!_skin.HasMaterial(Action))
        {
            CurrentFrameIndex = null;   // 真正缺素材 → 无帧（占位渲染由窗口层负责）
            return;
        }
        _loop.CachedCount = CachedFrameCount();
        // 按需重载（Swift frames(for:) 即加载触发点）：真实素材缓存被逐出（如睡眠预取把 Prefetched
        // 替换为 {Sleep} 后，Evict 清掉正在播放的 Yawn）→ CachedCount=0 且 Advance 不再推进
        // （OneShotFinished 永不置位 → 动作永久卡死；backfill 也被 PartialLoaded 拦截）→
        // 逐 tick 触发 FramesAsync 重新加载，加载落地后帧循环自动恢复衔接。
        if (_loop.CachedCount == 0 && _skin.HasRealMaterial(Action))
        {
            _ = _skin.FramesAsync(Action, _currentVariant);
        }
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

        // 5. 行为 AI（Swift 434-440）：仅在站立待机时决策，走动/回窝/睡眠/一次性动画期间不打断
        if (LifeState == PetLifeState.Roaming && !IsDragging && !_wasMusicPlaying
            && !_movingHome && _movementMode == PetMovementMode.Floor && Action == PetAction.Idle)
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
                $"dragging={IsDragging} movingHome={_movingHome} fading={_fading} " +
                $"floor={_movementMode == PetMovementMode.Floor} alpha={WindowAlpha:F2} " +
                $"walkTarget={(_walkTarget != null ? "set" : "nil")} idleCounter={_idleCounter:F1}");
        }
    }

    // MARK: - 交互 API（Swift 703-745）

    /// <summary>单击：戳一下。心情差会委屈（hurt），否则开心地蹦一下并减 1 心情（掉落/出窝中不响应）。
    /// 与 StartDrag 一致：任何交互都打断回窝过渡，否则宠物会以站立动画继续飘向屏幕边缘。</summary>
    public void Poke()
    {
        if (LifeState != PetLifeState.Roaming || IsDragging || _movementMode != PetMovementMode.Floor) return;
        CancelHomeTransition();
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

    /// <summary>抚摸（Swift 714-718）。同样打断回窝过渡（一致性：任何交互都中断回窝）。</summary>
    public void Pet()
    {
        if (LifeState != PetLifeState.Roaming || IsDragging) return;
        CancelHomeTransition();
        Happiness = Math.Min(100, Happiness + 12);
        SetAction(PetAction.Happy);
    }

    /// <summary>取消进行中的回窝过渡（movingHome/fading/walkTarget 复位，语义与 StartDrag 打断回窝一致）。</summary>
    private void CancelHomeTransition()
    {
        _movingHome = false;
        _fading = false;
        _walkTarget = null;
    }

    /// <summary>拎起（Swift 721-731）：打断任何小把戏，包括回窝（movingHome/fading 复位）。</summary>
    public void StartDrag()
    {
        if (LifeState != PetLifeState.Roaming) return;
        IsDragging = true;
        _walkTarget = null;
        _movementMode = PetMovementMode.Floor;
        DizzyAngle = 0;
        // 若正在回窝途中被拎起，必须复位回窝状态，否则 movingHome 永久卡住（Swift 727-729）
        _movingHome = false;
        _fading = false;
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

    /// <summary>被打断 → 立刻掉下来（Swift 580-588；movingHome/fading 一并复位）。</summary>
    internal void InterruptToFall()
    {
        _walkTarget = null;
        _movementMode = PetMovementMode.Falling;
        _fallVelocity = 0;
        _movingHome = false;
        _fading = false;
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

    // MARK: - 回窝 / 出窝（Swift 607-698）

    /// <summary>最近屏幕边缘回窝目标点（Windows 设计变更：不再走顶部中央，从当前坐标到最近边缘
    /// 左/右/上 直线距离最短者）。水平距离 = 到左/右边界的 |ΔX|，垂直距离 = 到上边界 |ΔY|；
    /// 水平更近（等距时水平优先）→ 目标 X = 边界（窗口边与边界对齐，沿用现有左下角位置语义），
    /// Y = 当前 Y；垂直更近 → X = 当前 X，Y = 上边界 − 窗口高 − 4（沿用现有顶边公式）。
    /// 左/右等距（水平居中）→ 取左。</summary>
    private (double X, double Y) HomeEdgeTarget(Rect screen, (double Width, double Height) size)
    {
        var pos = WindowPosition ?? InitialRoamingPosition();
        var leftX = screen.X;
        var rightX = screen.X + screen.Width;
        var topY = screen.Y + screen.Height;
        var dLeft = Math.Abs(pos.X - leftX);
        var dRight = Math.Abs(pos.X - rightX);
        var dTop = Math.Abs(pos.Y - topY);
        if (Math.Min(dLeft, dRight) <= dTop)
        {
            var x = dLeft <= dRight ? leftX : rightX - size.Width;
            return (x, pos.Y);
        }
        return (pos.X, topY - size.Height - 4);
    }

    /// <summary>走回最近屏幕边缘（左/右/上）→ 淡出 → AtHome（Swift 610-635 的 Windows 重设计：
    /// 目标点从"顶部中央"改为"距当前位置最近的屏幕边缘"，坐标对齐语义不变）。</summary>
    public void GoHome()
    {
        if (LifeState != PetLifeState.Roaming || IsDragging || _movingHome) return;
        var screen = CurrentScreen();
        _walkTarget = null;
        _movingHome = true;
        _fading = false;
        _movementMode = PetMovementMode.Floor;
        DizzyAngle = 0;
        var target = HomeEdgeTarget(screen, WindowSize);
        UpdateFacing(target);
        _walkTarget = target;
        SetAction(PetAction.Walk);
    }

    /// <summary>瞬间进窝：跳过走回动画，直接定位到最近屏幕边缘（从当前拖拽位置计算）并淡出
    /// （Swift 639-662 的 Windows 重设计，用于拖拽投放场景）。</summary>
    public void SnapHome()
    {
        if (LifeState != PetLifeState.Roaming || _movingHome) return;
        var screen = CurrentScreen();
        _walkTarget = null;
        _movingHome = true;
        _movementMode = PetMovementMode.Floor;
        DizzyAngle = 0;
        WindowPosition = HomeEdgeTarget(screen, WindowSize);
        // 直接进入淡出（不经走路），BeginHomeFadeout 会设 fading=true + 切 idle
        BeginHomeFadeout();
    }

    /// <summary>出窝（Swift 667-693）：从工作区顶上方 200pt 自由落体出场，
    /// 必须调用 ExitHomeOnlyMode 清除回窝节能模式（C# Prefetch 不自动清除，不调则出窝无帧可播）。</summary>
    public void LeaveHome()
    {
        if (LifeState != PetLifeState.AtHome || _movingHome) return;
        var screen = CurrentScreen();
        var size = WindowSize;
        // 出窝初始位置：宠物顶部大幅超出工作区顶（200pt 探出），视觉上像从屏幕顶端"探出来"再掉落
        WindowPosition = (screen.X + screen.Width / 2 - size.Width / 2,
                          screen.Y + screen.Height - size.Height + 200);
        WindowAlpha = 1;
        _movingHome = false;
        _fading = false;
        LifeState = PetLifeState.Roaming;
        // 自由落体出场
        _movementMode = PetMovementMode.Falling;
        _fallVelocity = 0;
        // 出窝：清除回窝节能模式，并确保 fall 动画能流式补齐（Swift 687-691）
        _skin.ExitHomeOnlyMode();
        SetAction(PetAction.Fall);
    }

    /// <summary>回窝走回动画完成后开始淡出（Swift 695-698）：fading=true + 切 idle。</summary>
    private void BeginHomeFadeout()
    {
        _fading = true;
        SetAction(PetAction.Idle);
    }

    // MARK: - 拖拽投放（Windows 托盘重设计：投放区 = 主屏工作区顶部中央 50% 宽 × 120pt 高矩形，
    // Swift 111-144 的刘海投放区语义替换；进入即触发 SnapHome）

    /// <summary>投放区矩形（Swift notchDropRegion 95-104 的 Windows 重设计）。</summary>
    private Rect DropZone()
    {
        var wa = _screens.PrimaryWorkArea;
        var width = wa.Width * 0.5;
        const double height = 120.0;
        return new Rect(wa.X + wa.Width / 2 - width / 2, wa.Y + wa.Height - height, width, height);
    }

    /// <summary>拖拽期间每帧调用：进入投放区（首次）→ 置 IsHoveringDropZone + 触发 SnapHome；
    /// 离开投放区 → 复位 IsHoveringDropZone（Swift 111-126 对应语义）。</summary>
    public void EvaluateNotchDropHover((double X, double Y) point)
    {
        var inside = InsideDropZone(point);
        if (inside && !_isHoveringDropZone)
        {
            IsHoveringDropZone = true;
            SnapHome();
        }
        else if (!inside && _isHoveringDropZone)
        {
            IsHoveringDropZone = false;
        }
    }

    /// <summary>拖拽松手后调用：返回宠物是否已进窝（true=已触发 SnapHome 且进窝成功，
    /// 调用方据此决定不再做落地动画；false=未进窝，宠物在松手位置落地）。</summary>
    public bool ExitNotchDropHover()
    {
        if (!_isHoveringDropZone) return false;
        IsHoveringDropZone = false;
        return LifeState == PetLifeState.AtHome;
    }

    private bool InsideDropZone((double X, double Y) point)
    {
        var zone = DropZone();
        return point.X >= zone.X && point.X <= zone.X + zone.Width
            && point.Y >= zone.Y && point.Y <= zone.Y + zone.Height;
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

    /// <summary>赋值并发布变更；返回是否有实际变化（LifeState 变化时同时触发 LifeStateChanged）。</summary>
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
