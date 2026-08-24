using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DeskPet.Core.Chat;
using DeskPet.Core.Pet;
using DeskPet.Core.Services;
using DeskPet.Windows.Services;
using DeskPet.Windows.ViewModels;

namespace DeskPet.Windows.Views;

/// <summary>
/// 宠物头顶气泡小窗（Task 5）：独立无边框透明窗口，跟随宠物移动、不抢焦点、
/// 不阻塞宠物动画。对应 Swift <c>BubblePanel</c>（PetBubbleManager.swift 62-66）+
/// <c>showWindow/hideWindow/repositionWindow</c>（421-483）。
/// 定位：Core 坐标（物理像素，y 向上）→ WPF Left/Top（DIP，y 向下），
/// 与 <see cref="PetWindow.ApplyPosition"/> 同一套换算；屏幕内约束逐行对照 Swift 460-483。
/// </summary>
public partial class BubbleWindow : Window
{
    private static readonly TimeSpan SpeechTickInterval = TimeSpan.FromSeconds(1);

    private readonly PetManager _pet;
    private readonly BubbleViewModel _vm;
    private readonly DispatcherTimer _tick;
    private readonly double _petWidthDip;
    private readonly double _petHeightDip;
    private readonly double _dpiScale;
    private bool _closed;

    /// <param name="pet">宠物状态中枢（WindowPosition/IsDragging/Action/LifeState 订阅）。</param>
    /// <param name="settings">设置（BubbleIntervalSeconds/BubbleMaxLength/Chat*）。</param>
    /// <param name="chat">对话客户端（AppServices.Chat；Task 7 换 RAG 管线后此处不变）。</param>
    /// <param name="petWidthDip">宠物窗宽（WPF DIP）。</param>
    /// <param name="petHeightDip">宠物窗高（WPF DIP）。</param>
    /// <param name="dpiScale">物理像素 / WPF DIP（与 PetWindow._dpiScale 同源）。</param>
    public BubbleWindow(
        PetManager pet, ISettingsService settings, IChatClient chat,
        double petWidthDip, double petHeightDip, double dpiScale)
    {
        InitializeComponent();
        _pet = pet;
        _petWidthDip = petWidthDip;
        _petHeightDip = petHeightDip;
        _dpiScale = dpiScale <= 0 ? 1.0 : dpiScale;

        var historyPath = Path.Combine(ChatServices.DataDirectory, "chat_history.json");
        _vm = new BubbleViewModel(pet, settings, chat, new ChatHistoryStore(historyPath));
        DataContext = _vm;

        _vm.ShowRequested += (_, _) => ShowBubble();
        _vm.HideRequested += (_, _) => Hide();
        _vm.PropertyChanged += OnVmPropertyChanged;

        pet.PropertyChanged += OnPetPropertyChanged;
        pet.LifeStateChanged += OnLifeStateChanged;

        _tick = new DispatcherTimer { Interval = SpeechTickInterval };
        _tick.Tick += (_, _) => _vm.OnSpeechTick(DateTime.UtcNow);
        _tick.Start();

        SizeChanged += (_, _) => Reposition();
        Reposition();
    }

    // MARK: - 宠物订阅（跟随 / 拖拽隐藏 / 动作台词 / 回窝清空）

    private void OnPetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PetManager.WindowPosition):
                Reposition();
                break;
            case nameof(PetManager.IsDragging):
                // Swift 213-222：拖拽时隐藏气泡，松手后恢复（有内容才恢复）。
                if (_pet.IsDragging) Hide();
                else if (_vm.HasContent) ShowBubble();
                break;
            case nameof(PetManager.Action):
                _vm.HandleActionChanged(_pet.Action, DateTime.UtcNow);
                break;
        }
    }

    /// <summary>回窝清空并隐藏（Swift 223-228：lifeState == .atHome → dismissBubble）。</summary>
    private void OnLifeStateChanged(PetLifeState state)
    {
        if (state == PetLifeState.AtHome) _vm.Dismiss();
    }

    // MARK: - 宠物窗可见性联动（托盘隐藏/显示时气泡跟随）

    public void HandlePetHidden() => Hide();

    public void HandlePetShown()
    {
        if (!_closed && _vm.HasContent && !_pet.IsDragging && _pet.LifeState == PetLifeState.Roaming)
        {
            ShowBubble();
        }
        else
        {
            Reposition();
        }
    }

    // MARK: - 显示 / 定位

    private void ShowBubble()
    {
        if (_closed) return;
        Reposition();
        if (!IsVisible) Show();
    }

    /// <summary>
    /// 核心坐标（物理像素，X=宠物窗左缘、Y=宠物窗底边，y 向上）→ WPF Left/Top（DIP，y 向下），
    /// Swift PetBubbleManager.repositionWindow（460-483）逐行移植：
    /// 默认在宠物正上方（间隙 4px），水平居中；屏幕内约束（边距 4px）；
    /// 上方空间不足且下方够 → 移到宠物下方；最后再整体钳制回屏幕内。
    /// </summary>
    private void Reposition()
    {
        if (_pet.WindowPosition is not { } pos) return;

        var bubbleW = ActualWidth > 0 ? ActualWidth : Width;
        var bubbleH = ActualHeight > 0 ? ActualHeight : 60;
        var petW = _petWidthDip * _dpiScale;    // 物理像素
        var petH = _petHeightDip * _dpiScale;
        var bubbleWpx = bubbleW * _dpiScale;
        var bubbleHpx = bubbleH * _dpiScale;

        var x = pos.X + petW / 2 - bubbleWpx / 2;
        var y = pos.Y + petH + 4;   // 默认在宠物上方

        var screen = CurrentScreenFor(pos.X, pos.Y);
        // 钳制用 Swift 的 min(max(v, low), high) 而非 Math.Clamp（low > high 时不抛异常）
        x = Math.Min(Math.Max(x, screen.X + 4), screen.X + screen.Width - 4 - bubbleWpx);
        var above = screen.Y + screen.Height - (pos.Y + petH) - 8;
        var below = pos.Y - screen.Y - 8;
        if (bubbleHpx > above && below >= above)
        {
            y = pos.Y - bubbleHpx - 4;   // 宠物上方放不下 → 放宠物下方
        }
        y = Math.Min(Math.Max(y, screen.Y + 4), screen.Y + screen.Height - 4 - bubbleHpx);

        Left = x / _dpiScale;
        Top = (VirtualBottomPx() - y) / _dpiScale - bubbleH;
    }

    /// <summary>宠物所在屏的工作区（Core 坐标，y 向上）；不在任何屏内 → 主屏（Swift 471）。</summary>
    private DeskPet.Core.Services.Rect CurrentScreenFor(double x, double y)
    {
        foreach (var wa in AppServices.Screen.WorkAreas)
        {
            if (x >= wa.X && x < wa.X + wa.Width && y >= wa.Y && y < wa.Y + wa.Height)
            {
                return wa;
            }
        }
        return AppServices.Screen.PrimaryWorkArea;
    }

    /// <summary>虚拟屏幕底边（WinForms 物理像素）——与 PetWindow.VirtualBottomPx 同一映射。</summary>
    private static double VirtualBottomPx()
    {
        var all = System.Windows.Forms.Screen.AllScreens;
        return all.Length == 0 ? 0 : all.Max(s => s.Bounds.Y + s.Bounds.Height);
    }

    // MARK: - 交互（点击开输入框 / 回车发送 / Esc 关闭）

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // 输入态点击空白 → 关闭（Swift toggleInput 语义）；点击输入框本身由 TextBox 处理（已 handled）。
        if (_vm.IsInputActive) _vm.CloseInput();
        else _vm.OpenInput();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BubbleViewModel.IsInputActive):
                // 输入态用 normal 档宽度（Swift activeSize = .normal）
                Width = _vm.IsInputActive ? 260 : 200;
                InputRow.Visibility = _vm.IsInputActive ? Visibility.Visible : Visibility.Collapsed;
                if (_vm.IsInputActive) FocusInput();
                break;
            case nameof(BubbleViewModel.UserText):
                UserTextBlock.Visibility = string.IsNullOrEmpty(_vm.UserText)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                break;
            case nameof(BubbleViewModel.InputText):
                SendButton.IsEnabled = !string.IsNullOrWhiteSpace(_vm.InputText);
                break;
        }
    }

    /// <summary>
    /// 输入框聚焦（Ruling 4）：ShowActivated=false 的窗口不抢焦点，但 TextBox 仍需键盘焦点才能打字；
    /// 点击手势内显式 Focus() + Keyboard.Focus()，并补延迟兜底（窗口 Show 后才布局）。
    /// </summary>
    private void FocusInput()
    {
        InputTextBox.Focusable = true;
        InputTextBox.Focus();
        Keyboard.Focus(InputTextBox);
        InputTextBox.CaretIndex = Math.Max(0, InputTextBox.Text.Length);
        Dispatcher.BeginInvoke(() =>
        {
            if (!_vm.IsInputActive) return;
            Keyboard.Focus(InputTextBox);
            InputTextBox.CaretIndex = Math.Max(0, InputTextBox.Text.Length);
        });
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        await _vm.SendAsync();
    }

    private async void OnInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _vm.SendAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CloseInput();
            Keyboard.ClearFocus();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _vm.Dismiss();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closed = true;
        _tick.Stop();
        _pet.PropertyChanged -= OnPetPropertyChanged;
        _pet.LifeStateChanged -= OnLifeStateChanged;
        base.OnClosing(e);
    }
}
