using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeskPet.Core.Pet;
using DeskPet.Windows.Services;

namespace DeskPet.Windows.Views;

public partial class PetWindow : Window
{
    private const PetAction Action = PetAction.Idle;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(16);

    private readonly WpfImageDecoder _decoder = new();
    private PetSkin? _skin;
    private PetAnimationLoop? _loop;
    private int _variant;
    private int _maxPixel;
    private DispatcherTimer? _timer;

    public PetWindow()
    {
        InitializeComponent();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "assets", "BoringPet");
        try
        {
            _skin = await PetSkin.LoadAsync(assetsDir, _decoder);
        }
        catch
        {
            _skin = null;
        }

        if (_skin == null)
        {
            PetImage.Visibility = Visibility.Collapsed;
            PlaceholderText.Visibility = Visibility.Visible;
            return;
        }

        _maxPixel = _skin.MaxPixelSize;
        var (width, height) = _skin.BaseFrameSize ?? (300, 300);
        Width = width;
        Height = height;
        PositionAtBottomCenter();

        _variant = _skin.PreferredVariant(Action) ?? 0;
        _loop = new PetAnimationLoop
        {
            Fps = _skin.Fps(Action),
            IsLoop = _skin.Loops(Action) < 0,
            TotalFrames = _skin.TotalFrameCount(Action, _variant)
        };
        _skin.Prefetch(new[] { Action });

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void PositionAtBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 40;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_skin == null || _loop == null) return;
        IReadOnlyList<DecodedFrame>? frames;
        try
        {
            frames = await _skin.FramesAsync(Action, _variant);
        }
        catch
        {
            return;
        }
        if (frames == null || frames.Count == 0) return;

        _loop.CachedCount = frames.Count;
        _loop.LoopPoint = _skin.LoopPoint(Action, _variant);
        if (_loop.Advance(1.0 / 60) && _loop.CurrentIndex is int idx && idx < frames.Count)
        {
            PetImage.Source = WpfImageDecoder.ToBitmapSource(frames[idx], _maxPixel);
        }
        _skin.BackfillIfNeeded(Action, _variant, _loop.CurrentIndex ?? 0);
    }
}