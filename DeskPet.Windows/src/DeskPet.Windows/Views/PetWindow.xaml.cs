using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
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
        PositionAtInitialRoaming();

        // 种子缓存：与 PetManager.SetAction(Idle) 的预取清单一致。不预取则缓存恒空、
        // 循环永不推进（CurrentFrameIndex 恒 null），宠物不会渲染。
        _skin.Prefetch(new[]
        {
            PetAction.Idle, PetAction.Walk, PetAction.Yawn,
            PetAction.Drag, PetAction.Happy, PetAction.Hurt, PetAction.Fall
        });

        _manager.PropertyChanged += OnPetPropertyChanged;
        _manager.LifeStateChanged += OnLifeStateChanged;

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

    private void PositionAtInitialRoaming()
    {
        // 与 PetManager.InitialRoamingPosition 一致：主屏水平居中，窗口底边在屏高 22% 处（y-up）。
        var area = AppServices.Screen.PrimaryWorkArea;
        var x = area.X + (area.Width - Width * _dpiScale) / 2;
        var y = area.Y + area.Height * 0.22;
        ApplyPosition((x, y));
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
        }
    }

    /// <summary>Core 坐标（物理像素，X=窗口左缘、Y=窗口底边，y 向上、底部=0）→ WPF Left/Top（DIP，y 向下）。</summary>
    private void ApplyPosition((double X, double Y) pos)
    {
        Left = pos.X / _dpiScale;
        Top = (VirtualBottomPx() - pos.Y) / _dpiScale - Height;
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
