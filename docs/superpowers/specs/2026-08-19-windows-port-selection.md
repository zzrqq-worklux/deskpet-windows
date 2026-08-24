# DeskPet Windows 移植 — 重构选型与技术设计

日期：2026-08-19
分支：`windows-adaptation`
状态：选型已确认，待评审

## 1. 背景与目标

DeskPet 是 macOS SwiftUI 桌面宠物应用（基于 boring.notch，GPLv3），功能 = 桌面宠物系统 + 刘海动态岛。目标是在 Windows 上提供等价体验，核心诉求是**桌面宠物**（序列帧动画、行为 AI、拖拽交互、LLM 对话、RAG 知识库）。

## 2. 选型结论（已确认）

| 维度 | 决策 |
|------|------|
| 技术栈 | **WPF (C#/.NET 8)** — 纯 dotnet CLI 可构建（无需 VS/管理员），透明置顶/托盘/多屏最成熟；~~WinUI 3~~ 因本机无管理员、VS Build Tools 无法安装而否决 |
| 范围 | **宠物优先 MVP** — 先移植宠物核心，刘海特色功能（日历/文件架/HUD）后置 |
| 复用 | **框架重写 + 逻辑移植** — UI 层用 WPF 重写；业务逻辑按其设计翻译为 C#，保持行为一致 |

> 否决项：SwiftUI 无 Windows 运行支持；Swift for Windows 无 UI 框架；WinUI 3/Uno/MAUI 依赖 VS Build Tools（本机不可装，需管理员写 ProgramData）；Tauri 的 Windows 构建需 MSVC 工具链（同样被 VS 卡住）；Electron 引入 web 栈、包体/内存大且 Core 逻辑需重写。**WPF 是唯一零工具链、纯 dotnet CLI、可直接复用 C# Core 层（59 项单测）的方案。**

## 3. 现有架构盘点（可移植性矩阵）

134 个 Swift 文件，按可移植性分三类：

### 3.1 纯逻辑（高可移植，直接翻译为 C#）

| 子系统 | 关键文件 | 说明 |
|--------|---------|------|
| 行为 AI 状态机 | `managers/PetManager.swift` | idle/walk/sleep/yawn/happy/hurt/drag/fall 流转、心情/清洁度、随机决策（~840 行，含物理掉落） |
| 帧动画引擎 | `components/Pet/SpriteAnimator.swift` | `PetSkin`：变体解析、降采样解码缓存、部分帧预缓存 + 流式补齐、loopPoint 无缝循环、回窝节能 trim（~840 行） |
| LLM 对话 | `managers/PetChatManager.swift` | OpenAI 兼容 API 流式调用、历史持久化、RAG 注入 |
| RAG 数据流 | `managers/RAGManager.swift` + `Providers/LocalVectorStore.swift` + `VectorStoreManager.swift` | 文档导入→分块→Embedding→本地向量库→检索 |
| 宠物气泡状态机 | `components/Pet/PetBubbleManager.swift` | 定时说话、对话窗口、换肤接口 |
| 素材规范 | `PET.md` + `config.json` 解析 | 动作目录 `idle_0/` `walk_0/`…、fps/loops/scale |

### 3.2 平台胶水（需替换为 Windows 对应物）

| 原能力 | macOS 依赖 | Windows 替代 |
|--------|-----------|-------------|
| 无边框置顶窗口 | `NSWindow`/`NSPanel`/`NSScreen`/`CGSSpace`(私有) | WPF `Window`：`WindowStyle=None` + `AllowsTransparency=true` + `Topmost=true`（分层窗口），多屏 `Screen.AllScreens` |
| 点击穿透/拖拽 | `NSEvent` 追踪、`PanGesture` | WPF 鼠标事件 / 全局钩子（`WH_MOUSE_LL`），`SetWindowLong` 点击穿透 |
| 系统托盘 | `MenuBarExtra`/`NSStatusBar` | `System.Windows.Forms.NotifyIcon` |
| 设置持久化 | `Defaults`(sindresorhus) | `Microsoft.UI.Xaml.ApplicationDataContainer` 或设置文件 |
| 音频播放 | `NSSound`/`AVAudioPlayer` | `Windows.Media.Playback.MediaPlayer` / NAudio |
| 键盘快捷键 | `KeyboardShortcuts`(sindresorhus) | 全局热键 `RegisterHotKey` / 快捷键框架 |
| 自启动 | `LaunchAtLogin` | 注册表 Run 键 / 启动任务 |

### 3.3 刘海专属功能（Windows 无菜单栏刘海，需重设计，MVP 后置）

- 音乐控制：`MediaControllers/*` + `MediaRemote` + `CoreAudio` + Metal 可视化 → Windows SMTC（`SystemMediaTransportControls`）
- 日历：`EventKit` + `CalendarManager` → Windows 日历 API/Outlook（重）
- 文件架 Shelf：`NSItemProvider`/`QuickLook`/`AirDrop` → Windows 拖放 + `Windows.Data` 剪贴板
- HUD 替换（音量/亮度/键盘背光）：`IOKit`/`CoreAudio` → Windows 音频端点 API / WMI（亮度难）
- 摄像头：`AVFoundation` → `Windows.Media.Capture`
- 更新：`Sparkle` → 自研/`Squirrel.Windows`
- 助手进程：XPC → 普通子进程 / 后台服务

## 4. 目标架构（WPF + MVVM）

```
DeskPet.Windows (WPF / .NET 8)
├── App.xaml(.cs)             入口、单实例、启动宠物窗口
├── Services/                 平台服务接口（窗口/托盘/音频/热键/设置）
├── Core/                      纯逻辑层（自包含，可单测）— 已交付（59 项测试）
│   ├── Pet/
│   │   ├── PetAction.cs / PetLifeState.cs
│   │   ├── PetSkin.cs         帧动画引擎（移植 SpriteAnimator.swift）
│   │   ├── PetSkinConfig.cs    config.json 解析
│   │   ├── SkinDirectoryScanner.cs / NaturalComparer.cs
│   │   ├── FrameSignatureComputer.cs
│   │   └── PetAnimationLoop.cs 帧推进状态机
│   ├── Chat/                  （Phase 2）
│   └── RAG/                   （Phase 2）
├── ViewModels/                MVVM（INotifyPropertyChanged + CommunityToolkit.Mvvm）
├── Views/                     XAML：主宠物窗口、设置窗、气泡、面板
└── assets/BoringPet/          内置素材（复用现有 PNG 序列帧）
```

关键原则：
- **Core 层零平台依赖**（不引用 WPF），保证行为/动画逻辑可直接移植并做单元测试（xUnit）— Phase 0 已交付并通过 59 项测试。
- 平台能力通过接口注入（`IWindowService`/`ITrayService`/`IAudioService`/`ISettingsService`…），与 Swift 版 `PetWindowController` 订阅 `windowPosition/windowAlpha` 的模式一一对应。
- 帧引擎中 `ImageIO`/`CGImage` 解码 → .NET `System.Windows.Media.Imaging`（WPF `BitmapImage`/`WriteableBitmap`，PNG 走 WIC）；签名/loopPoint/流式补齐算法已原样移植（Core 内测试通过）。

## 5. 平台能力映射（关键项）

| Swift 概念 | C#/WPF 对应 |
|-----------|--------------|
| `@Published` + ObservableObject | `INotifyPropertyChanged` + CommunityToolkit.Mvvm `[ObservableProperty]` |
| SwiftUI View | XAML Window/UserControl |
| `NSScreen.frame` 多屏 | `System.Windows.Forms.Screen` / `SystemParameters` |
| `Timer` on `.common` RunLoop（拖拽中动画不断） | `DispatcherTimer`（高优先级）或后台渲染线程（关键：拖拽期间不能卡动画） |
| `NSSound(contentsOf:)` 异步加载 | `MediaPlayer`/`SoundPlayer` 异步加载（避免阻塞 UI 线程） |
| `NSImage` 降采样 | `BitmapImage`（`DecodePixelWidth`）/ `WriteableBitmap`（PNG 经 WIC 解码） |
| `NotificationCenter.post(.openNotchToPet)` | 事件总线 / Messenger |
| `NSWindow`/`NSPanel` 无边框置顶 | WPF `Window`：`WindowStyle=None` + `AllowsTransparency=true` + `Topmost=true`（分层窗口） |
| `MenuBarExtra`/`NSStatusBar` | `System.Windows.Forms.NotifyIcon` |

## 6. 刘海概念的 Windows 重设计（MVP 阶段）

Windows 无菜单栏刘海，"回窝/进窝"载体改为**系统托盘**：
- 宠物拖拽/双击 → **回窝**：从当前位置走向**直线距离最近的屏幕边缘**（左/右/上，排除下边任务栏方向），到达边缘后淡出进窝（2026-08 用户裁决：macOS 灵动岛顶部中央是遗留概念，Windows 无对应物；出窝保持从顶部掉落）。
- 点击托盘 → 菜单：出窝 / 打开面板 / 设置 / 退出；单击托盘图标 → 出窝回桌面。
- 保留原行为：单击戳一戳、双击回窝/出窝、拖拽拎起、落地物理。
- 刘海投放区交互（拖到刘海打开面板）在 Windows 用"拖到屏幕顶部中央区域"触发进窝（可开关）。

## 7. MVP 范围与里程碑

### Phase 0 — 工程底座
- 建 WPF 解决方案、MVVM、依赖注入、托盘/单实例骨架
- 素材管线：PNG 解码 + 帧缓存框架（先接内置 BoringPet 素材）
- **验收**：能启动，无边框置顶透明窗口显示宠物（Core 层 59 项单测通过）

### Phase 1 — 宠物核心
- `PetManager` 状态机 + `PetSkin` 帧引擎（部分缓存/流式补齐/loopPoint）移植，xUnit 覆盖行为决策
- 交互：单击/双击/拖拽/落地物理/回窝淡出
- **验收**：宠物在桌面走动/待机/睡觉/被打扰，交互行为与 macOS 版一致

### Phase 2 — LLM + RAG
- `PetChatClient` 流式对话 + 气泡 UI + 历史持久化
- RAG：导入→分块→Embedding→本地向量→检索注入
- **验收**：对话气泡流式回复，RAG 能引用文档

### Phase 3 — 设置与打磨
- 设置页（皮肤目录/活动频率/音量/LLM/RAG 配置）、换肤热重载、托盘交互完善
- **验收**：功能对齐 macOS MVP 子集

### 后置（非 MVP）
- 媒体控制（SMTC）、日历、文件架、HUD、摄像头、自动更新

## 8. 风险与对策

| 风险 | 等级 | 对策 |
|------|------|------|
| 拖拽时动画停顿（WPF 消息泵） | 高 | 动画驱动放后台线程/高优先级 `DispatcherTimer`；Swift 版已有 `.common` 模式经验 |
| 无边框透明置顶窗口兼容性 | 中 | 用 `AppWindow` + 覆盖层；多屏 DPI 需逐步验证 |
| 帧解码性能（大序列帧） | 中 | 沿用降采样 + 部分缓存 + 流式补齐；用 WIC 异步解码 |
| 行为一致性 | 中 | Core 层用同一决策算法逐行翻译，xUnit 对照 Swift 行为 |
| 素材兼容性 | 低 | 复用 `PET.md` 规范，目录结构不改，内置 BoringPet 素材直接拷贝 |
| 全局热键/托盘点击穿透 | 中 | `RegisterHotKey`/`NotifyIcon` + 鼠标钩子，需实测 |

## 9. 决策结论（已确认）

| 决策项 | 结论 |
|--------|------|
| .NET / UI 框架 | **.NET 8 LTS + WPF**（纯 dotnet CLI 构建，无需 VS/管理员） |
| macOS 分支 | **不维护** — macOS 是另一贡献者的事；本分支 `windows-adaptation` 承载 Windows 版 |
| 仓库结构 | 见 §10 构建建议 |
| 打包 | 见 §10 打包建议 |

## 10. 构建与打包建议

### 10.1 仓库结构（建议，按此执行）

**Windows 版作为本仓库 `windows-adaptation` 分支下的自包含新项目**，位于 `DeskPet.Windows/`，与 macOS 源码隔离：

```
deskpet/                      ← 本仓库（windows-adaptation 分支）
├── docs/superpowers/...      ← 本设计与后续实施计划
├── (macOS 源码原样保留)      ← 不再由本分支维护
└── DeskPet.Windows/          ← 全新 WPF 解决方案（自包含，独立 csproj/sln）
    ├── src/DeskPet.Windows/      主项目（App/Core/Services/ViewModels/Views）
    ├── tests/DeskPet.Windows.Core.Tests/  Core 层 xUnit 单测
    ├── assets/BoringPet/          内置宠物素材（拷贝自 macOS 版 BoringPet）
    └── README.md
```

理由：
- 单一仓库、单一来源：素材/规范（PET.md）与文档就近；`windows-adaptation` 分支天然隔离 Windows 与 macOS 主线。
- macOS 主线（`main`）由另一贡献者维护，本分支不会反向污染；未来若 Windows 版独立发布，可整目录拆出为新仓库 `DeskPet.Windows`（git 保留历史）。
- 不采用独立仓库的原因：避免现在就重复建仓/推送；用户已在本仓库工作流内。

### 10.2 打包（MVP 建议，按此执行）

**MVP 用「免安装便携版」**：
- WPF 单文件/自包含发布（`dotnet publish -c Release -r win-x64 --self-contained`），输出可拷贝目录，含 exe + 依赖 + `assets/BoringPet`。
- 自启动通过注册表 `HKCU\...\Run`（用户级，无需管理员；与 macOS `LaunchAtLogin` 对应）。
- 更新策略 MVP 不实现（`Sparkle` 后置项）。

未来如需正式分发，再评估 MSIX 打包 / Squirrel.Windows 增量更新（Phase 后置）。

---

*本设计依据：README.md、PET.md、PetManager.swift、SpriteAnimator.swift、PetChatManager.swift、RAGManager.swift、deskpet.xcodeproj 依赖清单。*
