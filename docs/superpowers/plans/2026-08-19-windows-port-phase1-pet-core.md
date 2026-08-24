# DeskPet Windows 移植 Phase 1 — 宠物核心 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 macOS 版宠物核心行为完整搬到 Windows：`PetManager` 状态机（行为 AI / 走动物理 / 掉落 / 心情属性）、单击/双击/拖拽交互、回窝/出窝（托盘承载）、动作音效；宠物在桌面走动、待机、睡觉、被打扰，行为与 macOS 版一致。

**Architecture:** 复用 Phase 0 三层（Core 纯逻辑可单测 / WPF 应用层 / xUnit）。Phase 1 把 `PetManager.swift`（~840 行）移植到 `DeskPet.Core/Pet/PetManager.cs`，平台依赖全部抽象为接口注入（ISettingsService/IAudioService/IMusicService/IScreenInfo），由 WPF 层实现；PetWindow 的 60fps tick 改为驱动 PetManager（行为 AI + 物理 + 帧推进），窗口位置/透明度由 PetManager 发布、PetWindow 订阅移动。

**Tech Stack:** .NET 8、WPF（Phase 0 已建）、xUnit、CommunityToolkit.Mvvm（应用层，Phase 1 引入 MVVM）。

**Spec:** `docs/superpowers/specs/2026-08-19-windows-port-selection.md`（§5 能力映射、§6 托盘回窝、§7 Phase 1 里程碑）

## Global Constraints

- Core 层（`DeskPet.Core`）只依赖 BCL + System.Text.Json（`PetManager.cs` 不得引用 WPF/Forms）；`INotifyPropertyChanged` 用 BCL。
- 行为与 Swift `PetManager.swift` 逐行对应；Swift 里 `Defaults[...]` 全部走 `ISettingsService`。
- Swift 的 `NSScreen`/`NSSound`/`NSImage`/`NotificationCenter` 分别由 `IScreenInfo`/`IAudioService`/帧引擎/事件回调替代。
- 终审遗留（必须遵守）：出窝必须调 `PetSkin.ExitHomeOnlyMode()`（C# 的 `Prefetch` 不自动清除 HomeOnlyMode）；Phase 0 的 ~4% tick 漂移在此修正为实测 dt（Stopwatch）。
- 工作目录 `C:\Users\31177347\Desktop\Coding\deskpet`，分支 `windows-adaptation`；每个任务独立提交，禁止 `git add -A`。
- 许可：继承 GPLv3。

---

### Task 1: Core 服务接口 — ISettingsService / IAudioService / IMusicService / IScreenInfo

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Core/Services/ISettingsService.cs`
- Create: `DeskPet.Windows/src/DeskPet.Core/Services/IAudioService.cs`
- Create: `DeskPet.Windows/src/DeskPet.Core/Services/IMusicService.cs`
- Create: `DeskPet.Windows/src/DeskPet.Core/Services/IScreenInfo.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Services/ServiceContractsTests.cs`（接口契约冒烟）

**Interfaces:**
- Consumes: 无（纯接口定义）。
- Produces（Phase 1 各任务与 WPF 层实现方消费）：
  - `ISettingsService`：`double PetScale`、`double RoamInterval`、`double StatDecaySpeed`、`double SoundVolume`、`bool MusicDance`、`string SkinDirectory`（读写属性，对应 Swift `Defaults[.petScale]` 等）。
  - `IAudioService`：`void Play(string? path, double volume)`、`void Stop()`（异步内部实现，接口同步签名；无路径=停旧声）。
  - `IMusicService`：`bool IsPlaying { get; }` — **Phase 1 仅占位**（恒 false，音乐联动整体延后至 Phase 2 接 SMTC；本周期不做任何音乐功能，只保证 PetManager 移植可编译）。
  - `IScreenInfo`：`IReadOnlyList<Rect> WorkAreas`（主屏在前）、`Rect PrimaryWorkArea`；`record Rect(double X, double Y, double Width, double Height)`（Core 自定义，不用 WPF 类型）。
- 每个接口一条 XML 注释说明语义与 Swift 对应项。

- [ ] **Step 1: 写失败测试**（契约冒烟：接口存在、Rect 记录基本运算）
- [ ] **Step 2: 运行确认失败**（编译失败）
- [ ] **Step 3: 实现 4 接口 + Rect**
- [ ] **Step 4: 运行确认通过**（含既有 59 测试）
- [ ] **Step 5: Commit** — `feat(core): service contracts for pet manager port`

---

### Task 2: 应用层实现 — JsonSettingsService + WpfAudioService + StubMusicService + WpfScreenInfo

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Windows/Services/JsonSettingsService.cs`
- Create: `DeskPet.Windows/src/DeskPet.Windows/Services/WpfAudioService.cs`
- Create: `DeskPet.Windows/src/DeskPet.Windows/Services/StubMusicService.cs`（恒 false，音乐功能不做）
- Create: `DeskPet.Windows/src/DeskPet.Windows/Services/WpfScreenInfo.cs`
- Modify: `DeskPet.Windows/src/DeskPet.Windows/App.xaml.cs`（注册到 DI/静态容器）

**Interfaces:**
- Consumes: Task 1 接口；WPF/WinForms API。
- Produces: 四个接口的 WPF 实现：
  - `JsonSettingsService`：JSON 文件（`%APPDATA%/DeskPet/settings.json`），读写属性，默认值对应 Swift `Defaults` 默认（petScale=1.0、roamInterval 见 Swift、statDecaySpeed=1、soundVolume=1、musicDance=false、skinDirectory=""）。
  - `WpfAudioService`：`System.Windows.Media.MediaPlayer`（Media Foundation，支持 m4a）；`Play` 异步打开、设音量、播放；`Stop` 停止；动作切换时停旧声。
  - `StubMusicService`：`IsPlaying => false`（Phase 2 替换）。
  - `WpfScreenInfo`：`System.Windows.Forms.Screen.AllScreens` → Core `Rect` 列表（主屏在前）。
- 静态服务容器：App 启动构建实例并暴露（`App.Services` 简单静态字典即可，不引第三方 DI；MVVM 的 DI 完善放 Phase 3）。

- [ ] **Step 1: 失败测试**（对 JsonSettingsService 的读写/默认值/损坏文件容错 — 用临时目录；WpfScreenInfo 用 Forms 屏幕数断言 ≥1）
- [ ] **Step 2: 确认失败**
- [ ] **Step 3: 实现 4 个服务 + App 注册**
- [ ] **Step 4: 确认通过**（`dotnet build` + 全量测试）
- [ ] **Step 5: Commit** — `feat(windows): settings/audio/music/screen services`

---

### Task 3: Core PetManager 移植 — 状态与动作（无物理/无 AI）

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Core/Pet/PetManager.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Pet/PetManagerStateTests.cs`

**Interfaces:**
- Consumes: `PetAction`/`PetLifeState`（T2）、`PetSkin`（Phase 0）、`PetAnimationLoop`（Phase 0）、`ISettingsService`/`IAudioService`/`IMusicService`/`IScreenInfo`（T1）。
- Produces: `PetManager : INotifyPropertyChanged`（Core）：
  - 属性：`Action`、`FacingLeft`、`CurrentFrameIndex`（int?，-1 无帧）、`DizzyAngle`、`Happiness`、`Cleanliness`、`LifeState`、`IsDragging`、`WindowPosition`（(double X,double Y)?）、`WindowAlpha`。
  - 方法：`Tick(double dt)`（60fps 总入口，本任务先实现帧推进 + 动作切换部分）、`Poke()`、`Pet()`、`StartDrag()`、`EndDrag()`、`SyncWindowPosition`、`GoHome()`、`SnapHome()`、`LeaveHome()`、`ReloadSkin()`、`Start()`/`Stop()`。
  - 内部：`SetAction`（变体选择、音效触发经 IAudioService、`Prefetch` 调用、`MarkPlayed`）、`frames`（经 PetSkin）、动作切换时 `PetAnimationLoop.Reset()` + 配置（Fps/Loops/TotalFrames/CachedCount/LoopPoint 每 tick 更新）。
- 本任务范围：SetAction/帧推进/属性/生命周期；**不含**走动物理、行为 AI、回窝流程（T4/T5）。
- 构造：`PetManager(PetSkin skin, ISettingsService settings, IAudioService audio, IMusicService music, IScreenInfo screens)`；工厂 `static PetManager Load(...)` 包装 `PetSkin.LoadAsync`（用注入的 `IImageDecoder`）。

- [ ] **Step 1: 失败测试**（FakeServices：切动作→Action 变化 + AudioService 收到 Play(对应 sound)；循环动作帧索引按 fps 推进；变体切换；属性默认值 happiness=100/cleanliness=100；ReloadSkin）
- [ ] **Step 2: 确认失败**
- [ ] **Step 3: 实现**（逐行对照 Swift `PetManager.swift` 相应段：属性 40-85、animTick 帧推进 359-432、setAction 761-802、音效 804-839）
- [ ] **Step 4: 确认通过**
- [ ] **Step 5: Commit** — `feat(core): port PetManager state and action switching`

---

### Task 4: Core PetManager — 走动/掉落物理 + 行为 AI + 属性衰减

**Files:**
- Modify: `DeskPet.Windows/src/DeskPet.Core/Pet/PetManager.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Pet/PetManagerBehaviorTests.cs`

**Interfaces:**
- Consumes: T3 的 PetManager；`IScreenInfo`（走动目标/地面/回窝目标点计算）。
- Produces（T3 基础上补齐）：
  - `Tick(dt)` 完整：音乐分支**保持惰性占位**（`IMusicService.IsPlaying` 恒 false，该分支永不激活，音乐功能 Phase 2 再做）、移动（walk 目标插值 / falling 重力）、透明度渐变（回窝淡出/出窝淡入）、帧推进（T3）、行为 AI（仅 idle 时决策）、每 5s 诊断日志（`System.Diagnostics.Debug`）。
  - 行为 AI：`decideNextBehavior`/`startWalking`/`startSleeping`/`randomWalkTarget`/`updateFacing`/`interruptToFall`/`land`（Swift 452-605 全量移植；运动模式 PetMovementMode 中 climbing/hanging 等遗留分支直接回落 .floor）。
  - 属性衰减 `statTick`（Swift 749-754），由 Tick 内部 60s 累计触发。
- 关键语义（Swift 对齐）：walk 速度 70pt/s、重力 500、落地弹跳 -0.25；目标点避开顶部 6% 与底部 10%+Dock 区；`movingHome` 兜底复位（目标丢失清状态）。

- [ ] **Step 1: 失败测试**（FakeScreenInfo 固定工作区：walk 目标到达→回 idle；掉落落地→fall 动画继续播完；行为 AI 随机决策用注入 Random（可种子）；happiness 衰减；musicDance 设置 true 但 IsPlaying=false 不跳舞）
- [ ] **Step 2: 确认失败**
- [ ] **Step 3: 实现**（对照 Swift 452-605、257-330、749-754；`Random` 可注入便于测试）
- [ ] **Step 4: 确认通过**
- [ ] **Step 5: Commit** — `feat(core): port pet movement, physics, behavior AI`

---

### Task 5: Core PetManager — 回窝/出窝流程 + 拖拽投放（托盘重设计）

**Files:**
- Modify: `DeskPet.Windows/src/DeskPet.Core/Pet/PetManager.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Pet/PetManagerHomeTests.cs`

**Interfaces:**
- Consumes: T3/T4 的 PetManager；`IScreenInfo`。
- Produces:
  - `GoHome()`/`SnapHome()`：目标点 = **距当前位置直线距离最近的屏幕边缘**（左/右/上，排除下边任务栏；2026-08 用户裁决，替换原"屏幕顶部中央"灵动岛遗留语义）→ 走到边缘（宠物边缘平齐）→ 淡出 → `LifeState = AtHome`（Swift 610-662 骨架；目标点计算见已实现的 `HomeEdgeTarget`：水平距离（到左/右边界的 |ΔX|）vs 垂直距离（到上边界 |ΔY|）取小者；平局规则：水平优先、左优先）。
  - `LeaveHome()`：从顶部上方 200pt 自由落体出场 + **必须调用 `PetSkin.ExitHomeOnlyMode()`**（终审遗留，否则回窝 trim 后出窝无帧可播）；落地点=工作区底部+窗口高 6%。
  - 淡出/淡入：`WindowAlpha` 每 tick ±0.06（Swift 331-357）。
  - 拖拽投放：`EvaluateNotchDropHover(point)` / `ExitNotchDropHover()` — Windows 重设计：把"刘海投放区"改为"屏幕顶部中央区域"（工作区宽 50% × 高 120pt 矩形）；拖入→触发 `SnapHome()`+发 `HomeRequested` 事件（应用层打开托盘菜单提示或直接进窝）；拖出→复位。
  - 事件：`event Action? HomeStateChanged`（AtHome 进出时触发，应用层用于托盘图标状态）。
- 注意：`GoHome`/`SnapHome`/`LeaveHome` 的 guard 逻辑与 Swift 一致（roaming/atHome 校验）。

- [ ] **Step 1: 失败测试**（FakeScreenInfo：GoHome→walk 到顶→alpha 渐降→AtHome；SnapHome 直接淡出；LeaveHome→falling 落回地面→Roaming + ExitHomeOnlyMode 被调用（用可观察 skin 替身或计数）；投放区判定）
- [ ] **Step 2: 确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 确认通过**
- [ ] **Step 5: Commit** — `feat(core): port pet home/leave flow with tray redesign`

---

### Task 6: PetWindow 接线 — 驱动 PetManager + 窗口移动/透明度/帧渲染

**Files:**
- Modify: `DeskPet.Windows/src/DeskPet.Windows/Views/PetWindow.xaml.cs`
- Modify: `DeskPet.Windows/src/DeskPet.Windows/Views/PetWindow.xaml`（如需）

**Interfaces:**
- Consumes: `PetManager`（T3-T5）、`PetSkin`、`WpfImageDecoder`（Phase 0）、`PetAnimationLoop`（经 PetManager）。
- Produces: PetWindow 完整运行时：
  - 启动：`PetManager.Load`（assets/BoringPet + 服务）；失败→占位提示。
  - 60fps `DispatcherTimer`：**用 `Stopwatch` 实测 dt**（终审遗留：修正 Phase 0 的 ~4% 漂移）→ `petManager.Tick(dt)`；随后 `skin.BackfillIfNeeded(action, variant, petManager.CurrentFrameIndex)`（沿用 Phase 0 调用点）。
  - 订阅 `PropertyChanged`：`WindowPosition` → 窗口 `Left/Top`；`WindowAlpha` → `Opacity`；`Action`/`CurrentFrameIndex` → 从 `skin.FramesAsync` 取帧渲染（RGBA→BitmapSource，沿用 Phase 0 转换）；`LifeState == AtHome` → `Hide()`；回 Roaming 且应显示 → `Show()`。
  - 位置语义：Swift 坐标原点=屏幕左下、y 向上；WPF `Left/Top`=屏幕左上、y 向下 → `Top = workAreaBottom - (pos.Y + windowHeight)` 换算（`IScreenInfo.PrimaryWorkArea`）。
  - 窗口尺寸沿用 Phase 0（BaseFrameSize×petScale，经 `ISettingsService.PetScale`）。

- [ ] **Step 1: 失败测试**（坐标换算辅助函数抽为 Core 或内部静态方法并单测：屏幕坐标↔WPF 坐标双向；PetWindow 集成靠手动验证）
- [ ] **Step 2: 实现**
- [ ] **Step 3: 构建 + 全量测试**
- [ ] **Step 4: 启动验证**（宠物自动走动/待机/睡觉/打哈欠；窗口跟随移动；透明度渐变正常）
- [ ] **Step 5: Commit** — `feat(windows): wire pet window to PetManager engine`

---

### Task 7: 鼠标交互 — 单击/双击/拖拽

**Files:**
- Modify: `DeskPet.Windows/src/DeskPet.Windows/Views/PetWindow.xaml.cs`

**Interfaces:**
- Consumes: `PetManager`（T3-T5）。
- Produces:
  - 单击：`MouseLeftButtonUp` + `ClickCount == 1` 且非拖拽 → `Poke()`（Swift：心情<30 → hurt，否则 happy + happiness-1）。
  - 双击：`ClickCount == 2` → 回窝/出窝切换（roaming→`GoHome()`；atHome→`LeaveHome()`）。
  - 拖拽：按下记起点+阈值（≥5px 判定拖拽）→ `StartDrag()` + 鼠标捕获；`MouseMove` → `SyncWindowPosition(光标换算坐标)` + `EvaluateNotchDropHover`；松开 → `ExitNotchDropHover()`（若进窝）或 `EndDrag()`。
  - 拖拽期间窗口跟随鼠标（每帧移动），不触发单击（拖拽标志置位）。
  - 注意 WPF 双击与拖拽竞争：用时间/移动距离判定，参照 Swift `DragDetector.swift` 语义。

- [ ] **Step 1: 失败测试**（交互判定逻辑抽 Core 内部辅助（点击/拖拽分类器）并单测；窗口事件集成手动验证）
- [ ] **Step 2: 实现**
- [ ] **Step 3: 构建 + 测试 + 启动手动验证**（单击戳、双击回窝/出窝、拖拽拎起/松手、拖到顶部中央→进窝）
- [ ] **Step 4: Commit** — `feat(windows): pet mouse interactions`

---

### Task 8: 托盘集成 — 在家状态 + 出窝

**Files:**
- Modify: `DeskPet.Windows/src/DeskPet.Windows/Services/TrayService.cs`
- Modify: `DeskPet.Windows/src/DeskPet.Windows/App.xaml.cs`

**Interfaces:**
- Consumes: `PetManager`（`HomeStateChanged` 事件）、`TrayService`（Phase 0）。
- Produces:
  - 订阅 `HomeStateChanged`：AtHome → 托盘菜单「出窝」启用、宠物窗隐藏；Roaming → 反。
  - 托盘单击/双击：roaming 时隐藏宠物（相当于 macOS 点击托盘头像进窝）；atHome 时 `LeaveHome()` 出窝。
  - 托盘菜单增加「出窝/回窝」项（状态随 LifeState 切换文字）。
  - 现有「显示/隐藏宠物」保留（纯隐藏=不改变 LifeState）。

- [ ] **Step 1: 实现**
- [ ] **Step 2: 构建 + 测试 + 启动手动验证**（回窝后托盘状态变化、单击出窝掉落）
- [ ] **Step 3: Commit** — `feat(windows): tray home/leave integration`

---

### Task 9: 文档清理 — Phase 0 计划重写 + DeskPet.Windows README

**Files:**
- Modify: `docs/superpowers/plans/2026-08-19-windows-port-phase0-foundation.md`（终审遗留：内容仍描述 WinUI 3，重写为 WPF 事实，避免后续照抄错误 csproj）
- Create: `DeskPet.Windows/README.md`（构建/运行/目录说明）

- [ ] **Step 1: 重写 Phase 0 计划文档**（保留任务结构，更新 Goal/Architecture/Tech Stack/Task 1 csproj 为 WPF；标注 WPF 裁决日期）
- [ ] **Step 2: 写 DeskPet.Windows/README.md**（`dotnet build`/`dotnet test`/运行方式、目录结构、素材说明）
- [ ] **Step 3: Commit** — `docs: align phase 0 plan with WPF stack; add windows readme`

---

### Task 10: Phase 1 验收 — 行为对照矩阵

**Files:**
- Create: `docs/superpowers/plans/2026-08-19-windows-port-phase1-verification.md`（验收清单，不提交代码）

**Interfaces:**
- Consumes: 全部 Phase 1 交付。
- Produces: 手动验收记录（对照 macOS 版行为）：
  - 待机：idle 循环 + 变体轮换 + 偶发哈欠/开心/受惊
  - 走动：随机走动、朝向镜像（向左走自动镜像素材）、到点停
  - 睡觉：哈欠→入睡→冻结末帧数秒→醒来；心情低时更频繁
  - 交互：单击戳（心情好蹦/差委屈 + 面板占位）、双击回窝/出窝、拖拽拎起挣扎/松手落下、拖到顶部中央→进窝
  - 回窝/出窝：走回顶淡出→托盘「在家」；托盘单击→掉落出场落地弹跳
  - 属性：清洁度随时间下降、抚摸恢复心情
  - 音效：动作切换 sound.m4a 播放、音量设置生效

- [ ] **Step 1: 运行应用逐项验收**（记录实际 vs 期望；差异→修复轮）
- [ ] **Step 2: 修复验收发现的问题**（小问题直接修复轮；大问题回对应 Task 修复）
- [ ] **Step 3: 验收全绿后** — 进入全分支终审（复用 Phase 0 流程）

---

## Self-Review

**1. Spec coverage（§7 Phase 1 + §6）**
- PetManager 状态机 + 行为 AI → T3/T4 ✓
- 交互 单击/双击/拖拽/落地物理/回窝淡出 → T4/T5/T7 ✓
- 回窝/出窝（托盘承载）→ T5/T8 ✓
- 音效 → T3（IAudioService 触发）+ T2（实现）✓
- 终审遗留（ExitHomeOnlyMode、tick 漂移）→ T5/T6 显式要求 ✓
- MVVM/DI → T2 静态容器 + 应用层 MVVM 起步（完整 DI 留 Phase 3，终审已定）✓

**2. Placeholder 扫描**
- 无 TBD/TODO；每个任务有文件、接口、测试/验证步骤；行为以 Swift `PetManager.swift` 行号为参照。

**3. 类型一致性**
- `ISettingsService` 等 4 接口 T1 定义，T2 实现、T3-T8 消费，签名固定；`PetManager` 公开成员 T3 定义，T4-T8 扩展并消费，跨任务一致。
- `PetAnimationLoop`/`PetSkin`/`PetAction` 沿用 Phase 0 签名，无漂移。
- `Rect`（Core 自定）避免 WPF 类型泄漏进 Core — 关键约束。

**待跟进（Phase 2 计划）**：LLM 对话气泡（PetBubbleManager/PetChatManager）、RAG、音乐联动（SMTC 实现 IMusicService）、媒体控制。
