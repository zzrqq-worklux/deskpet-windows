# Phase 1 验收记录 — 行为对照矩阵

- **任务：** Task 10 — Phase 1 验收（deskpet Windows 移植，WPF）
- **日期：** 2026-08-21
- **环境：** 无头自动化会话（1560x1040 虚拟屏，**系统 DPI 缩放 200%**，会话中可见真实桌面应用运行）
- **基线：** HEAD `8d7b5d7`，构建 0 错误，测试 **146/146 通过**
- **验证手段：** DBWIN 捕获 `[Pet]` 5s 状态诊断（action/lifeState/dragging/movingHome/walkTarget）、SendInput 注入（单击/拖拽）、托盘 UIA 菜单调用、GetWindowRect/IsWindowVisible 轮询、PrintWindow 截帧 + vision 姿态确认

---

## 验收矩阵

| # | 验收项（期望） | 结果 | 验证过程与证据 |
|---|---|---|---|
| 1 | **待机**：idle 循环 + 变体轮换 + 偶发小动作（哈欠/开心/受惊） | **OK**（变体轮换：部分 — 帧变化已确认，变体身份视觉不可分） | 3.5 分钟观察：长 Idle 段（53-78s / 123-128s 等）+ 偶发小动作全出现：独立哈欠（t=193.8 action=Yawn→Idle，未入睡）、开心（稳定运行 t=87/97/142 action=Happy）、受惊（t=182/272 action=Hurt）。PrintWindow 截图对比确认 idle 帧持续变化（同角色不同嘴型/眼神帧）。变体轮换由 Core 决策路径保证（DecideNextBehavior reshuffle 分支 + 4 个 idle 变体），视觉区分 idle_0..3 不可行（素材姿态差异细微） |
| 2 | **走动**：随机走动、朝向镜像（向左走自动镜像素材）、到点停 | **走动 OK / 朝向镜像 FAIL（Finding F1）** | walkTarget=set 期间窗口位置持续移动（runA：L 774→1246→277→495→962），到点 walkTarget=nil + 回 Idle（t=123.5/183.7），行为正确。**朝向镜像未实现**：Core 正确置 FacingLeft（单测覆盖），但窗口层从不翻转素材（对照 Swift `scaleEffect(x: facingLeft ? -1 : 1)`）；视觉证据：左右向走动截帧尾巴均在左侧 |
| 3 | **睡觉**：哈欠→入睡→冻结末帧数秒→醒来 | **OK** | 多次完整循环（runA t=28.3 Yawn→33.3 Sleep→48.3 Idle；t=128.5/153.6 重复）；睡眠保持 5-15s；**冻结末帧视觉确认**：睡眠中相隔 9s 的两帧完全一致；睡眠姿态（带泡泡）vision 确认；睡眠动作由哈欠过渡（无跳变） |
| 4 | **交互**：单击戳（心情好蹦/差委屈）、双击回窝/出窝、拖拽拎起挣扎/松手落下、拖到顶部中央→进窝 | **单击 OK / 拖拽 OK / 投放 OK / 双击 待真机** | 单击×3 → action=Happy（DBWIN 确认）+ 蹦跳帧；连续戳 ~50 次至 happiness<30 后 → action=Hurt（委屈帧 vision 确认）。拖拽：SendInput 按下-移动-松开，窗口左下角落在投放点 err=(0,0)，拖拽中 pose=拎起挣扎（vision 确认），松手 action=Drag→Idle。拖到顶部中央投放区（光标 (780,60) 保持）→ 0.25s 内 SnapHome 淡出完成→AtHome→窗口隐藏，托盘菜单文字切为「出窝」。**双击宠物窗回窝/出窝：本无头会话输入管线不产生 WM_LBUTTONDBLCLK（WPF ClickCount 恒 1，T6/T8 已知限制）→ 待真机**；同一条 GoHome/LeaveHome 路径已由托盘侧全量验证（见 #5） |
| 5 | **回窝/出窝**：走回顶部淡出→托盘「在家」；托盘→掉落出场落地弹跳 | **OK**（托盘驱动全链路；落点视觉受 DPI 环境影响，见 Finding F2） | 托盘 UIA 菜单「出窝」invoke → LeaveHome：窗口出现、action=Fall、自由落体→落地弹跳（速度>300 反弹 25%）→ 稳定停在地面（Core 落点 floorY=6.3 正确）；「回窝」invoke → GoHome：走动→淡出→AtHome→窗口隐藏→托盘菜单切「出窝」。LifeState 切换与菜单文字联动全通过。Core 层落地物理（弹跳/停稳）由 DBWIN+位置轨迹确认正确 |
| 6 | **属性**：清洁度随时间下降、抚摸恢复心情 | **间接验证（待面板）** | StatTick 每 60s：Cleanliness −0.8×speed、Happiness 趋向 50（+4% 差）——代码路径运行 5 分钟无异常；数值断言由 Core 单测覆盖（146/146）；Phase 1 无属性面板，外部不可观测数值 → 数值展示待 Phase 3 面板真机确认 |
| 7 | **音效**：动作切换 sound.m4a 播放、音量设置生效 | **待真机** | WpfAudioService 已接线（专用 STA 线程 MediaPlayer，SetAction 播 sound.m4a、音量 clamp 0-1；素材 m4a 齐全）；无头会话无法听音 → 待真机手动确认（T2 音频 harness 已锻炼过路径） |
| 8 | **稳定性**：连续运行 5 分钟无卡死 | **OK** | 两轮验证：(a) 5.3 分钟连续运行，`[Pet]` 诊断 5s 一条连续 64 条无中断（Core 卡死回归未复现），期间 AI 正常轮换（会话中有人为拖拽/退出干扰，非缺陷）；(b) 干净 5 分钟运行：20 次存活轮询全部 alive+窗口可见，59 条诊断连续，t=298s 仍在走动。无冻结、无崩溃、无卡死 |

---

## Findings（发现的问题，本任务不修复）

### F1（缺陷）：朝向镜像未在窗口层实现
- 验收项 2「向左走自动镜像素材」不满足。Core 的 `FacingLeft` 正确置位（`PetManager.UpdateFacing`，单测覆盖），但窗口层 `PetWindow` 渲染时不应用翻转（对照 Swift `PetContentView.swift:24 .scaleEffect(x: pet.facingLeft ? -1 : 1)`），且渲染固定变体 0（T6 已知限制）。视觉确认：右向与左向走动截帧中尾巴均在左侧（未镜像）。
- 建议：修复轮补 `PetImage.RenderTransform = ScaleX(-1)`（或等效），并纳入回归。

### F2（环境放大缺陷/已知限制）：DPI-unaware + 坐标缩放不一致 → 缩放会话下宠物渲染位置偏移
- 本会话系统 DPI 缩放 200%，进程 DPI awareness=0（`GetProcessDpiAwareness`=0）。实测（临时只读探针，已移除）：WPF 窗口 Left/Top（DIP）与 Core 物理坐标间 `_dpiScale` 换算在部分路径不一致（宽度换算用 dpiScale=2.0，位置换算在启动/拖拽/坠落阶段观察到不一致因子），导致宠物实际渲染位置偏移（本会话中偏右/偏下，出窝落点落在可视屏幕下方；Core 物理层落点本身正确 — 探针确认停稳于 floorY=6.3）。
- 这是 T6 已标注「单 DPI 假设」限制的强表现，非本次新增回归；100% DPI 真机不受影响。建议：后续阶段统一 DPI awareness（PerMonitorV2 声明已有）或在窗口层统一坐标系；真机 200% 缩放环境补验出窝落点。

---

## 诚实限制（无头会话无法验证项）

| 项 | 原因 | 处理 |
|---|---|---|
| 双击宠物窗回窝/出窝 | 无头会话注入管线不产生 WPF ClickCount=2（T6/T8 已知） | 待真机手动确认；同路径已由托盘侧端到端验证 |
| 音效播放/音量 | 无头无法听音 | 待真机手动确认 |
| 属性数值衰减可视化 | Phase 1 无面板 | 单测+代码路径间接验证，待面板（Phase 3） |
| 变体轮换的视觉身份 | idle_0..3 素材差异细微，截帧视觉不可区分 | Core 决策路径+单测覆盖，帧变化已确认 |
| 出窝落点视觉位置 | F2（200% DPI 会话坐标偏移） | Core 物理正确；真机补验 |
| idle 小动作 Happy/Hurt 偶发捕捉 | 5s 诊断采样可能漏捕 1s 级动作 | 用长时运行捕获（4 次 Happy/2 次 Hurt 已捕获）补足 |

## 验证环境数据

- 构建：`dotnet build DeskPet.Windows.sln -c Debug` → 0 错误；测试 146/146 通过。
- 验证脚本与证据（截图/日志）：`%TEMP%\opencode\task10\*`（runA 行为观察、runB 交互、stab/stab2 稳定性、hurt 委屈分支、probe 探针轨迹）。
- 临时探针（Debug.WriteLine，只读）验证后已全部移除，工作树干净（`git status` 无改动）。
