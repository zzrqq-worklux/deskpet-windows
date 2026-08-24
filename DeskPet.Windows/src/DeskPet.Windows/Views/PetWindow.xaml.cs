using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskPet.Core.Pet;
using DeskPet.Windows.Services;

namespace DeskPet.Windows.Views;

public partial class PetWindow : Window
{
    // Core 的 PetManager.CurrentVariant 为 internal（仅测试程序集可见），窗口层无法读取；
    // 按 brief 允许的取舍用固定变体 0（帧索引由 PetManager 驱动，素材帧数在变体间一致时可对齐）。
    private const int FrameVariant = 0;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(16);

    private readonly Stopwatch _stopwatch = new();

    private PetSkin? _skin;
    private PetManager? _manager;
    private int _maxPixel;
    private double _dpiScale = 1.0;   // 物理像素 / WPF DIP（主屏缩放；Phase 1 单缩放假设，多屏混合 DPI 为已知限制）
    private DispatcherTimer? _timer;
    private bool _hiddenForHome;

    // MARK: - 鼠标交互状态（T7：单击/双击/拖拽）

    private System.Windows.Point _mouseDownClient;   // 按下时窗口客户区坐标（拖拽起点）
    private DateTime _mouseDownTime;                 // 按下时间戳（Swift DragDetector 状态对齐；双击分类以 WPF ClickCount 为准）
    private bool _leftDownInsideWindow;  // 本窗口内按下且未松开
    private bool _mouseDragActive;       // 拖拽已启动（位移 ≥5px），启动后不再触发单击
    private (double X, double Y)? _grabOffset;   // 拖拽抓取偏移（Core 坐标：抓取点 − 窗口底左）；保持宠物在抓取点下

    public PetWindow()
    {
        InitializeComponent();
    }

    /// <summary>接入运行时（App.xaml.cs 异步加载皮肤后调用）；skin 加载失败 → 占位提示（沿用 Phase 0）。</summary>
    public void Attach(PetSkin? skin, PetManager? manager)
    {
        _skin = skin;
        _manager = manager;

        if (_skin == null || _manager == null)
        {
            PetImage.Visibility = Visibility.Collapsed;
            PlaceholderText.Visibility = Visibility.Visible;
            return;
        }

        _maxPixel = _skin.MaxPixelSize;
        _dpiScale = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width / SystemParameters.PrimaryScreenWidth;
        if (_dpiScale <= 0) _dpiScale = 1.0;
        var (baseWidth, baseHeight) = _skin.BaseFrameSize ?? (300, 300);
        Width = baseWidth * AppServices.Settings.PetScale / _dpiScale;
        Height = baseHeight * AppServices.Settings.PetScale / _dpiScale;
        // 与 Core 对齐：管理器需要知道窗口实际位置（否则拖拽抓取偏移无基准、回退为光标锚定）
        _manager.SyncWindowPosition(PositionAtInitialRoaming());

        // 种子缓存：与 PetManager.SetAction(Idle) 的预取清单一致。不预取则缓存恒空、
        // 循环永不推进（CurrentFrameIndex 恒 null），宠物不会渲染。
        _skin.Prefetch(new[]
        {
            PetAction.Idle, PetAction.Walk, PetAction.Yawn,
            PetAction.Drag, PetAction.Happy, PetAction.Hurt, PetAction.Fall
        });

        _manager.PropertyChanged += OnPetPropertyChanged;
        _manager.LifeStateChanged += OnLifeStateChanged;

        // 初始朝向（Swift scaleEffect(x: -1) 对应：FacingLeft=true → 水平镜像素材）
        ApplyFacing(_manager.FacingLeft);

        _stopwatch.Restart();
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // MARK: - 坐标换算

    /// <summary>
    /// 虚拟屏幕底边（WinForms 物理像素）：与 WpfScreenInfo 的映射完全一致
    /// （max over screens of bounds.Y + bounds.Height），保证 Core y-up 坐标精确反演；
    /// 物理像素 → WPF DIP 的缩放由 <see cref="_dpiScale"/> 处理。
    /// </summary>
    private static double VirtualBottomPx()
    {
        var all = System.Windows.Forms.Screen.AllScreens;
        return all.Length == 0 ? 0 : all.Max(s => s.Bounds.Y + s.Bounds.Height);
    }

    private (double X, double Y) PositionAtInitialRoaming()
    {
        // 与 PetManager.InitialRoamingPosition 一致：主屏水平居中，窗口底边在屏高 22% 处（y-up）。
        var area = AppServices.Screen.PrimaryWorkArea;
        var x = area.X + (area.Width - Width * _dpiScale) / 2;
        var y = area.Y + area.Height * 0.22;
        ApplyPosition((x, y));
        return (x, y);
    }

    // MARK: - 引擎驱动（60fps 计时器）

    private void OnTick(object? sender, EventArgs e)
    {
        if (_manager == null) return;
        var dt = _stopwatch.Elapsed.TotalSeconds;   // 实测 dt，修正 Phase 0 固定 1/60 的 ~4% 漂移
        _stopwatch.Restart();
        _manager.Tick(dt);
        if (_manager.CurrentFrameIndex is { } frameIndex)
        {
            _skin?.BackfillIfNeeded(_manager.Action, FrameVariant, frameIndex);
        }
        // 兜底再播种（Core 已知问题，窗口层缓解）：SetAction(Yawn) 走入睡路径（_pendingSleep=true）时
        // Prefetch({Sleep}) 会清空 Prefetched 并把当前动作 Yawn 从淘汰保护名单中剔除；随后的 Evict
        // （由 sleep 自身加载完成触发）会删掉 Cache[Yawn] → CachedFrameCount 恒 0 → Advance 提前返回、
        // CurrentFrameIndex 不再发布（帧停在旧值）、OneShotFinished 永不置位 → 永久卡死。
        // 每 30 tick 对当前动作调一次 FramesAsync：缓存缺失/为空时它会启动一次完整加载（StartLoad），
        // 加载完成后循环自动恢复；缓存健康时只是一次加锁查找，开销可忽略。
        if (++_reseedCounter % 30 == 0)
        {
            try { _ = _skin?.FramesAsync(_manager.Action, FrameVariant); }
            catch { }
        }
    }

    private int _reseedCounter;

    // MARK: - PropertyChanged 订阅

    private void OnPetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_manager == null) return;
        switch (e.PropertyName)
        {
            case nameof(PetManager.WindowPosition):
                if (_manager.WindowPosition is { } pos) ApplyPosition(pos);
                break;
            case nameof(PetManager.WindowAlpha):
                Opacity = _manager.WindowAlpha;
                break;
            case nameof(PetManager.Action):
            case nameof(PetManager.CurrentFrameIndex):
                _ = RenderCurrentFrameAsync();
                break;
            case nameof(PetManager.FacingLeft):
                ApplyFacing(_manager.FacingLeft);
                break;
        }
    }

    /// <summary>
    /// 朝向镜像（Swift PetContentView scaleEffect(x: pet.facingLeft ? -1 : 1, y: 1, anchor: .center) 对应）：
    /// 素材只需画一个方向（如朝右），朝左时水平翻转。RenderTransform 以元素中心为原点（XAML 已设
    /// RenderTransformOrigin=0.5,0.5），翻转后仍保持在窗口内，不影响布局。
    /// </summary>
    private void ApplyFacing(bool facingLeft)
    {
        PetImage.RenderTransform = new ScaleTransform(facingLeft ? -1 : 1, 1);
    }

    /// <summary>Core 坐标（物理像素，X=窗口左缘、Y=窗口底边，y 向上、底部=0）→ WPF Left/Top（DIP，y 向下）。</summary>
    private void ApplyPosition((double X, double Y) pos)
    {
        Left = pos.X / _dpiScale;
        Top = (VirtualBottomPx() - pos.Y) / _dpiScale - Height;
    }

    /// <summary>
    /// WPF 窗口客户区坐标 → Core 坐标（物理像素，X=窗口左缘、Y=窗口底边，y 向上、底部=0）。
    /// 与 <see cref="ApplyPosition"/> 同一套换算的反演：X = PointToScreen(p).X；
    /// coreY = <see cref="VirtualBottomPx"/> − PointToScreen(p).Y
    /// （即拖拽时窗口底边跟随光标，PointToScreen 返回设备像素、屏幕坐标原点=虚拟桌面左上）。
    /// </summary>
    private (double X, double Y) PointToCore(System.Windows.Point p)
    {
        var screen = PointToScreen(p);
        return (screen.X, VirtualBottomPx() - screen.Y);
    }

    // MARK: - 鼠标交互（T7：单击 Poke / 双击 回窝·出窝 / 拖拽 拎起·投放）

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_manager == null || _manager.LifeState != PetLifeState.Roaming) return;
        _mouseDownClient = e.GetPosition(this);
        _mouseDownTime = DateTime.UtcNow;
        _leftDownInsideWindow = true;
        _mouseDragActive = false;
        _grabOffset = null;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_manager == null || _manager.LifeState != PetLifeState.Roaming) return;
        if (e.LeftButton != MouseButtonState.Pressed || !_leftDownInsideWindow) return;

        var pos = e.GetPosition(this);
        if (!_mouseDragActive)
        {
            // 拖拽判定先于单击判定：位移 ≥5px 才启动拖拽（Swift DragDetector 语义），否则视为待定点击
            if (!PetInteractionClassifier.IsDragGesture(
                    (_mouseDownClient.X, _mouseDownClient.Y), (pos.X, pos.Y),
                    PetInteractionClassifier.DefaultDragThresholdPx)) return;
            // 抓取偏移在阈值跨越处（拖拽真正启动点）记录：抓取点相对窗口底左的 Core 偏移。
            // 此后 SyncWindowPosition(光标 − 偏移) → 宠物保持在抓取点下方（Swift 语义），
            // 且启动瞬间 target == 当前窗口位置（不跳变）。窗口位置未知时退化为光标锚定（旧行为）。
            _grabOffset = _manager.WindowPosition is { } winPos
                ? PetDragGrab.Offset(PointToCore(pos), winPos)
                : null;
            _manager.StartDrag();
            _mouseDragActive = true;
            CaptureMouse();   // 捕获后窗口在光标移出边界时仍持续收到 MouseMove
        }

        var core = PointToCore(pos);
        if (_grabOffset is { } offset) core = PetDragGrab.Target(core, offset);
        _manager.SyncWindowPosition(core);
        // 投放区判定用光标本身（Swift evaluateNotchDropHover(at: mouse) 语义）
        _manager.EvaluateNotchDropHover(PointToCore(pos));
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_manager == null) return;

        if (_mouseDragActive)
        {
            // 拖拽优先：先问投放区，已进窝则跳过落地动画（EndDrag）
            var droppedHome = _manager.ExitNotchDropHover();
            if (!droppedHome) _manager.EndDrag();
            _mouseDragActive = false;
            _leftDownInsideWindow = false;
            _grabOffset = null;
            ReleaseMouseCapture();
            return;
        }

        if (!_leftDownInsideWindow) return;
        _leftDownInsideWindow = false;

        var kind = PetInteractionClassifier.Classify(e.ClickCount, isDragging: false);
        switch (kind)
        {
            case ClickKind.Single:
                _manager.Poke();
                break;
            case ClickKind.Double:
                if (_manager.LifeState == PetLifeState.Roaming) _manager.GoHome();
                else _manager.LeaveHome();
                break;
        }
    }

    // MARK: - 捕获丢失兜底（fix round：拖拽收尾）

    /// <summary>
    /// 捕获丢失收尾：拖拽中窗口被隐藏（如拖进投放区后淡出回窝完成 → AtHome → Hide()，
    /// 隐藏持有捕获的窗口会释放捕获，后续 MouseUp 不再到达本窗口）、捕获被系统/其他窗口抢占
    /// （Alt+Tab 等）时，MouseUp 永远不来 → 若不在此收尾，manager.IsDragging 永久置位，
    /// Poke/Pet/GoHome/StartDrag 全部 no-op，宠物冻结直至重启。
    /// 语义与正常松手一致：先问投放区（已进窝 → 跳过 EndDrag，IsDragging 保持=已回家），
    /// 否则 EndDrag 落地。
    /// </summary>
    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishDragOnCaptureLoss();
    }

    /// <summary>失焦兜底：LostMouseCapture 之外的失焦路径（如仅键盘焦点被夺走）。
    /// 与 OnLostMouseCapture 共用收尾，守卫幂等——两条都触发时只收尾一次。</summary>
    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        FinishDragOnCaptureLoss();
    }

    private void FinishDragOnCaptureLoss()
    {
        if (_manager == null || !_mouseDragActive) return;
        var droppedHome = _manager.ExitNotchDropHover();
        if (!droppedHome) _manager.EndDrag();
        _mouseDragActive = false;
        _leftDownInsideWindow = false;
        _grabOffset = null;
    }

    private async Task RenderCurrentFrameAsync()
    {
        if (_skin == null || _manager == null) return;
        var action = _manager.Action;
        var index = _manager.CurrentFrameIndex;
        if (index == null) return;

        IReadOnlyList<DecodedFrame>? frames;
        try
        {
            frames = await _skin.FramesAsync(action, FrameVariant);
        }
        catch
        {
            return;
        }
        if (frames == null || frames.Count == 0) return;
        // await 期间动作/帧已变 → 丢弃过期帧
        if (_manager.Action != action || _manager.CurrentFrameIndex != index) return;
        if (index.Value < frames.Count)
        {
            PetImage.Source = WpfImageDecoder.ToBitmapSource(frames[index.Value], _maxPixel);
        }
    }

    private void OnLifeStateChanged(PetLifeState state)
    {
        if (state == PetLifeState.AtHome)
        {
            _hiddenForHome = true;
            Hide();
        }
        else if (state == PetLifeState.Roaming && _hiddenForHome)
        {
            _hiddenForHome = false;
            Show();
        }
    }
}
