# DeskPet Windows 移植 Phase 2 — 验收记录

日期：2026-08-24
分支：`windows-adaptation`
前置：Phase 2 实现完成（Tasks 1-7，300 项单测通过，0 警告）

## 验收清单

> 真机手动验收为主。每项记录 通过 / 失败 / 未测（注明原因）。
> 运行方式：`dotnet run --project DeskPet.Windows/src/DeskPet.Windows`（从 `DeskPet.Windows/` 目录）
> 或直接运行 `src/DeskPet.Windows/bin/Debug/net8.0-windows/DeskPet.Windows.exe`。

### 1. 设置页 Chat 配置
- [ ] 设置 → 聊天：勾选 ChatEnabled
- [ ] 填写 Base URL（如 `https://api.deepseek.com/v1`）、API Key、Model（如 `deepseek-chat`）
- [ ] System Prompt 可编辑（默认人设文案存在）
- [ ] 点击 Test Connection → 显示成功或带错误信息的失败（不崩溃）

### 2. 气泡对话（流式）
- [ ] 点击气泡 → 出现输入框（ShowActivated=false 下焦点可用）
- [ ] 输入文字回车 → 气泡显示"…" → 流式逐字回复（打字机效果）
- [ ] 回复完成后气泡保留一段时间后消失
- [ ] 历史保留：重启应用后上次对话仍在（上限 ChatHistoryCount=20）
- [ ] 对话中宠物动画不卡顿

### 3. 自言自语
- [ ] 待机约 BubbleIntervalSeconds（默认 120s，±25% 抖动）出现随机台词
- [ ] 台词与当前动作匹配（走动/睡觉/开心/受伤等）
- [ ] 显示约 8 秒后自动消失
- [ ] 输入/对话进行中不自动说话

### 4. RAG 知识库
- [ ] 设置 → RAG：勾选 RagEnabled，填写 EmbeddingModel
- [ ] 导入 1-2 个 .txt/.md 文档 → 列表出现（来源名/分块数/时间）
- [ ] 重启应用 → 文档列表仍在（注册表持久化）
- [ ] 对话提问文档相关内容 → 回复引用文档内容（注入生效）
- [ ] 删除单文档 / 清空 → 列表与向量库同步
- [ ] 重复导入同名文件 → 旧分块被替换（不重复）

### 5. 错误处理
- [ ] 断网或错误的 Base URL → 气泡显示友好错误，应用不崩溃
- [ ] API Key 错误 → Test Connection 显示错误信息
- [ ] 空回复 → "模型没有返回内容"类提示

### 6. 气泡跟随与交互
- [ ] 气泡随宠物移动（走动/拖拽时跟随）
- [ ] 气泡不抢焦点、不干扰宠物拖拽/单击/双击
- [ ] 宠物回窝 → 气泡隐藏；出窝恢复

### 7. 稳定性
- [ ] 长对话（20+ 轮）+ 导入 3 个文档后运行 5 分钟无卡死/崩溃
- [ ] 系统托盘 显示/隐藏/退出 正常

## 记录

| # | 结果 | 现象/备注 |
|---|------|-----------|
| 1 | 待补 | 设置页 Chat 配置（Test Connection 需真实端点） |
| 2 | 待补 | 气泡对话流式/历史/焦点（无头受限项：真机复验） |
| 3 | 部分 | 宠物尺寸 DPI 问题已发现并修复（见修复轮 FIX-A）；自言自语待真机复验 |
| 4 | 待补 | RAG 导入/检索需真实 Embedding 端点 |
| 5 | 待补 | 错误处理需断网/错 Key 实测 |
| 6 | 待补 | 气泡跟随/交互需真机复验 |
| 7 | 待补 | 稳定性长跑 |

> 截至 2026-08-24：Phase 2 收尾。用户真机验收启动后发现宠物过小（200% DPI）→ 修复轮 FIX-A 已修复并实测（120 DIP = 240px，见下）。其余清单项由用户后续补充记录。

## 修复轮

### FIX-A：宠物在 200% DPI 下过小
- 现象：宠物渲染仅 105 物理像素（base×scale），与 DPI 无关；macOS 版按 pt 计且有 120pt 最小高度下限 → 高 DPI 屏上 Windows 宠物只有 macOS 的一半多。
- 修复：`IScreenInfo.DpiScale` + `PetManager.WindowSize = base × max(PetScale×skinScale, 120/baseHeight) × DpiScale`（与 Swift windowSize 对齐）；窗口与 Core 共享单一公式。
- 提交：`e8cdb02` fix(windows): pet size follows dpi and swift minimum height
- 复验：进程 DPI awareness=2（PerMonitorV2）；宠物窗口实测 120×120 DIP = 240 物理像素 @200%，屏幕居中 ✓

## 终审

（Phase 2 全分支终审结果；复用 Phase 0/1 流程）
