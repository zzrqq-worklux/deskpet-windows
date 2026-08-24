using System.ComponentModel;
using System.IO;
using System.Windows;
using DeskPet.Core.Chat;
using DeskPet.Core.Pet;
using DeskPet.Core.RAG;
using DeskPet.Core.Services;
using DeskPet.Windows.Services;
using DeskPet.Windows.Views;

namespace DeskPet.Windows;

/// <summary>
/// Phase 1 静态服务容器（Phase 3 再引入 DI）。
/// </summary>
public static class AppServices
{
    public static ISettingsService Settings { get; } = new JsonSettingsService();

    public static IAudioService Audio { get; } = new WpfAudioService();

    public static IMusicService Music { get; } = new StubMusicService();

    public static IScreenInfo Screen { get; } = new WpfScreenInfo();

    /// <summary>
    /// 对话客户端（Ruling 7：IChatClient；Ruling 4b：重指向 RAG 注入管线——
    /// 包装共享 PetChatClient 实例，其同时充当 IEmbeddingService，故可写）。
    /// </summary>
    public static IChatClient Chat { get; set; } =
        new RagChatPipeline(ChatServices.Chat, ChatServices.Embedding, ChatServices.VectorStore, Settings);

    /// <summary>RAG 编排服务（Task 6 设置页导入/删除/检索用）。</summary>
    public static RAGManager Rag { get; } = ChatServices.Rag;
}

public partial class App : System.Windows.Application
{
    private PetWindow? _petWindow;
    private SettingsWindow? _settingsWindow;
    private TrayService? _tray;
    private PetManager? _manager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _ = AppServices.Settings.PetScale;
        _ = AppServices.Screen.WorkAreas;
        _ = AppServices.Music.IsPlaying;
        _ = AppServices.Chat;
        _ = AppServices.Rag;
        AppServices.Audio.Stop();

        _petWindow = new PetWindow();
        _petWindow.Closing += OnPetWindowClosing;

        _settingsWindow = new SettingsWindow();

        _tray = new TrayService(
            showPet: () =>
            {
                _petWindow?.Show();
                _petWindow?.Activate();
            },
            hidePet: () => _petWindow?.Hide(),
            goHome: () => _manager?.GoHome(),
            leaveHome: () => _manager?.LeaveHome(),
            showSettings: () =>
            {
                _settingsWindow?.Show();
                _settingsWindow?.Activate();
            },
            exit: OnExitRequested);

        _petWindow.Show();
        _ = StartPetAsync();
    }

    /// <summary>异步加载皮肤并创建 PetManager（T6 接线）；加载失败 → 窗口层显示占位提示。</summary>
    private async Task StartPetAsync()
    {
        var petWindow = _petWindow;
        if (petWindow == null) return;

        var assetsDir = Path.Combine(AppContext.BaseDirectory, "assets", "BoringPet");
        PetSkin? skin = null;
        try
        {
            skin = await PetSkin.LoadAsync(assetsDir, new WpfImageDecoder());
        }
        catch
        {
            skin = null;
        }

        PetManager? manager = null;
        if (skin != null)
        {
            manager = PetManager.Load(skin, AppServices.Settings, AppServices.Audio, AppServices.Music, AppServices.Screen);
            // T8：托盘菜单「回窝/出窝」文字随 LifeState 切换（handler 只改文字，不读帧 — T5 LeaveHome 瞬态安全）。
            manager.LifeStateChanged += OnManagerLifeStateChanged;
        }
        _manager = manager;
        petWindow.Attach(skin, manager);
    }

    private void OnManagerLifeStateChanged(PetLifeState state)
    {
        _tray?.SetAtHome(state == PetLifeState.AtHome);
    }

    private void OnPetWindowClosing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current.Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        _petWindow?.Hide();
    }

    private void OnExitRequested()
    {
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        base.OnExit(e);
    }
}
