# DeskPet (Windows)

**DeskPet Windows 版** — 基于 macOS 版 [DeskPet](https://github.com/2048Nemo/DeskPet) 的跨平台移植。一只无边框、透明置顶、会自己走动/睡觉/打哈欠的桌面宠物，支持任意序列帧素材换肤。

- **技术栈**：WPF（`.NET 8`、`net8.0-windows`）、纯 dotnet CLI 构建（无需 Visual Studio / VS Build Tools）
- **架构**：`DeskPet.Core` 纯逻辑层（可单测） + `DeskPet.Windows` WPF 应用层，框架决策详见 `docs/superpowers/plans/2026-08-19-windows-port-phase0-foundation.md`

## 构建与运行

前置要求：.NET 8 SDK（`dotnet --version` 确认）。

```bash
# 构建整个解决方案
dotnet build DeskPet.Windows.sln

# 运行单元测试（Core 层纯逻辑 + 平台服务）
dotnet test tests/DeskPet.Windows.Core.Tests/DeskPet.Windows.Core.Tests.csproj

# 运行应用
dotnet run --project src/DeskPet.Windows

# 或直接运行编译产物
./src/DeskPet.Windows/bin/Debug/net8.0-windows/DeskPet.Windows.exe
```

启动后桌面会出现透明置顶的宠物（加载 `assets/BoringPet` 内置素材）；右键托盘图标可显示/隐藏、回窝/出窝、打开设置、退出。

## 目录结构

```
DeskPet.Windows/
├── DeskPet.Windows.sln              # 解决方案
├── src/
│   ├── DeskPet.Core/                # 纯逻辑层（net8.0，零 UI 依赖，可单测）
│   │   └── Pet/                     # PetAction/PetSkin/PetManager/帧引擎等
│   └── DeskPet.Windows/             # WPF 应用（UseWPF=true + UseWindowsForms=true）
│       ├── App.xaml                 # System.Windows.Application 入口
│       ├── Services/                # WpfImageDecoder/WpfAudioService/TrayService 等平台实现
│       └── Views/                   # PetWindow（透明置顶）/ SettingsWindow
├── tests/
│   └── DeskPet.Windows.Core.Tests/  # xUnit 测试（Core + 平台服务）
└── assets/
    └── BoringPet/                   # 内置宠物素材（每个动作一个序列帧文件夹）
```

## 素材（皮肤）

素材规范见仓库根目录 **[PET.md](../PET.md)**：每个动作是一个子文件夹（如 `idle_0/`、`walk_0/`），内含编号 PNG 序列帧，可选 `config.json` 调帧率/缩放。

- 自定义皮肤放在 `~/Pictures/BoringPet/` 即可被识别（与应用内置素材同规范）
- 变体目录（`<动作>_<编号>`）自动归并到同一动作并随机轮换

## 当前功能状态

- [x] **Phase 0 — 工程底座**：解决方案、Core 层骨架、帧引擎（预缓存/流式补齐/loopPoint/回窝节能）
- [x] **Phase 1 — 宠物核心**：行为 AI 状态机（idle/walk/sleep/yawn/happy/hurt/drag/fall/home）、鼠标交互（单击/双击/拖拽）、托盘（显示/隐藏、回窝/出窝、设置/退出）
- [ ] **Phase 2+ 规划**：LLM 对话（OpenAI 兼容接口）、RAG 知识库、音乐联动、设置页完善

各阶段计划：`docs/superpowers/plans/2026-08-19-windows-port-phase0-foundation.md`（Phase 0）、`docs/superpowers/plans/2026-08-19-windows-port-phase1-pet-core.md`（Phase 1）。

## 许可

继承 [GPLv3](../LICENSE)，与 macOS 原版一致。
