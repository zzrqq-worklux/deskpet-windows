using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using DeskPet.Core.Services;

namespace DeskPet.Windows.Services;

/// <summary>
/// 音效播放服务。线程模型：专用后台 STA 线程（自有 Dispatcher）上创建和操作
/// MediaPlayer —— MediaPlayer 是 DispatcherObject，必须在其创建线程上使用；
/// 每次 Play 在后台线程新建实例、关闭旧实例。Play/Stop 仅向该线程投递消息
/// （BeginInvoke，非阻塞），避免阻塞 UI 线程（Swift 端 NSSound 初始化可阻塞 ~0.4s）。
/// </summary>
public sealed class WpfAudioService : IAudioService
{
    private readonly object _lock = new();
    private Dispatcher? _dispatcher;
    private MediaPlayer? _current;

    public void Play(string? path, double volume)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Stop();
            return;
        }

        var dispatcher = GetDispatcher();
        dispatcher.BeginInvoke(new Action(() =>
        {
            StopCurrent();
            var player = new MediaPlayer();
            _current = player;
            player.Open(new Uri(Path.GetFullPath(path)));
            player.Volume = Math.Clamp(volume, 0.0, 1.0);
            player.Play();
        }), DispatcherPriority.Normal);
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_dispatcher is null) return;
            _dispatcher.BeginInvoke(new Action(StopCurrent), DispatcherPriority.Normal);
        }
    }

    private void StopCurrent()
    {
        _current?.Stop();
        _current?.Close();
        _current = null;
    }

    private Dispatcher GetDispatcher()
    {
        lock (_lock)
        {
            if (_dispatcher is null)
            {
                using var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                });
                thread.IsBackground = true;
                thread.Name = "DeskPetAudio";
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait();
            }
            return _dispatcher!;
        }
    }
}
