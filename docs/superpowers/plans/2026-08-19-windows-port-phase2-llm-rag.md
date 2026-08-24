# DeskPet Windows 移植 Phase 2 — LLM 对话 + RAG 知识库 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 macOS 版的 LLM 对话与 RAG 知识库搬到 Windows 版：宠物头顶气泡自言自语 + 与任意 OpenAI 兼容 API 流式对话（OpenAI/DeepSeek/Ollama…），导入文档生成向量、对话自动检索引用；设置页提供完整配置。

**Architecture:** 沿用 Phase 0/1 三层（Core 纯逻辑可单测 / WPF 应用层 / xUnit）。LLM 客户端与 RAG 数据流移植到 `DeskPet.Core`（纯 HTTP + 逻辑，可单测，Fake 网络注入），气泡 UI 为附着在宠物窗上方的独立 WPF 小窗，设置项扩展 `JsonSettingsService`。依赖注入沿用 `AppServices` 静态容器（Phase 3 再完善 DI）。

**Tech Stack:** .NET 8、WPF、xUnit、`System.Net.Http`（SSE 流式解析手写，不引第三方）、`System.Text.Json`。

**Spec:** `docs/superpowers/specs/2026-08-19-windows-port-selection.md`（§7 Phase 2 里程碑：LLM 对话 + RAG；§5 能力映射）

## Global Constraints

- Core（`DeskPet.Core`）只依赖 BCL + System.Text.Json；HTTP 用 `HttpClient`（经注入，可 Fake）；`INotifyPropertyChanged` 用 BCL。
- 行为与 Swift 对齐：`PetChatManager.swift`（OpenAI 兼容 `chat/completions` + `embeddings`、流式 SSE、错误映射、历史持久化）、`RAGManager.swift`（导入→分块→向量→写库）、`LocalVectorStore.swift`（本地 JSON + 余弦相似度）、`PetBubbleManager.swift`/`PetBubbleView.swift`（气泡状态机与渲染）。
- 配置项（扩展 `ISettingsService`）：`ChatEnabled`、`ChatBaseUrl`、`ChatApiKey`、`ChatModel`、`ChatSystemPrompt`、`ChatHistoryCount`、`BubbleIntervalSeconds`、`BubbleMaxLength`、`EmbeddingModel`、`RagEnabled`。
- 音乐（SMTC）按用户指示**延后**，Phase 2 不做（`IMusicService` 保持恒 false 占位）。
- 已知遗留必须遵守：`PetSkin` 播放中动作缓存可能被淘汰 → 对话/气泡不得依赖帧缓存语义；气泡窗口不阻塞宠物动画。
- 工作目录 `C:\Users\31177347\Desktop\Coding\deskpet`，分支 `windows-adaptation`；每个任务独立提交，禁止 `git add -A`。
- 许可：继承 GPLv3。

---

### Task 1: Core 对话/RAG 契约 — ChatMessage / IChatClient / IEmbeddingService / ISettingsService 扩展

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Core/Chat/ChatMessage.cs`
- Create: `DeskPet.Windows/src/DeskPet.Core/Chat/IChatClient.cs`
- Create: `DeskPet.Windows/src/DeskPet.Core/Chat/IEmbeddingService.cs`
- Create: `DeskPet.Windows/src/DeskPet.Core/RAG/VectorDocument.cs` + `VectorSearchResult.cs` + `IVectorStore.cs`
- Modify: `DeskPet.Windows/src/DeskPet.Core/Services/ISettingsService.cs`（扩展上述聊天/RAG 配置属性）
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Chat/ContractsTests.cs` + `Rag/ContractsTests.cs`

**Interfaces:**
- Consumes: `ISettingsService`（T1 扩展后）。
- Produces（签名固定，后续任务消费）：
  - `record ChatMessage(Guid Id, ChatRole Role, string Content, bool IsStreaming)`；`enum ChatRole { User, Assistant, System }`（对应 Swift `ChatMessage`，Defaults.Serializable → Core 可序列化）。
  - `interface IChatClient { Task<string> StreamChatAsync(IReadOnlyList<ChatMessage> history, string userInput, IProgress<string>? progress, CancellationToken ct); }` — 流式返回完整回复（增量经 progress 上报）；`Task<bool> TestConnectionAsync(CancellationToken ct)`。
  - `interface IEmbeddingService { Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct); }`。
  - `record VectorDocument(string Id, string Text, float[]? Embedding)`；`record VectorSearchResult(string DocumentId, string Text, float Score)`；`interface IVectorStore { Task UpsertAsync(IReadOnlyList<VectorDocument> docs, CancellationToken ct); Task<IReadOnlyList<VectorSearchResult>> SearchAsync(string query, float[]? queryEmbedding, int topK, CancellationToken ct); Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct); Task ClearAsync(CancellationToken ct); }`（对应 Swift `VectorStoreProviding`）。
  - `ISettingsService` 新增属性（读写，默认值对应 Swift `Defaults`）：`bool ChatEnabled`(false)、`string ChatBaseUrl`("")、`string ChatApiKey`("")、`string ChatModel`("")、`string ChatSystemPrompt`(默认人设文案，参照 Swift)、`int ChatHistoryCount`(20)、`double BubbleIntervalSeconds`(120)、`string EmbeddingModel`("")、`bool RagEnabled`(false)。
- 每条接口 XML 注释说明 Swift 对应项。

- [ ] **Step 1: 写失败测试**（契约冒烟：类型存在、ChatMessage 序列化往返、ISettingsService 新属性存在性）
- [ ] **Step 2: 确认失败**（编译失败）
- [ ] **Step 3: 实现**
- [ ] **Step 4: 确认通过**（含既有 182）
- [ ] **Step 5: Commit** — `feat(core): chat and rag contracts`

---

### Task 2: Core PetChatClient — OpenAI 兼容流式对话 + Embedding

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Core/Chat/PetChatClient.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Chat/PetChatClientTests.cs`（FakeHttpMessageHandler 注入）

**Interfaces:**
- Consumes: `IChatClient`/`IEmbeddingService`（T1）、`ISettingsService`（T1 扩展）、`HttpClient`（注入）。
- Produces: `PetChatClient : IChatClient, IEmbeddingService`：
  - `StreamChatAsync`：POST `{BaseUrl}/chat/completions`，`model`、`messages`（history + user）、`stream: true`；Bearer 认证（Key 为空则省略）；SSE 行解析（`data: {...}`，`choices[0].delta.content` 累积，`[DONE]` 结束）；每增量 `progress.Report`；错误映射（Swift `PetChatError`：NotEnabled/NotConfigured/InvalidURL/HTTP 错误/EmptyResponse）。
  - `TestConnectionAsync`：简单非流式请求或首个 chunk 验证。
  - `EmbedAsync`：POST `{BaseUrl}/embeddings`，`model`=EmbeddingModel，批量（Swift 分批逻辑），返回 `data[].embedding`。
  - `BaseUrl` 补全：仅填域名时自动补 `/v1`（Swift 语义）；URL 合法性校验。
  - 超时：HttpClient 默认超时 + `CancellationToken`。
- 关键语义：与 Swift `PetChatManager.swift` 逐行对照（endpoint 拼接、请求体、SSE 解析、错误分支、分批 embedding）。

- [ ] **Step 1: 写失败测试**（FakeHttpMessageHandler 模拟 SSE 流：增量上报顺序、完整回复、`[DONE]` 终止、HTTP 400 → 错误、空回复 → EmptyResponse、BaseUrl 补全 `/v1`、Bearer 头有无；Embedding 分批与解析）
- [ ] **Step 2: 确认失败**
- [ ] **Step 3: 实现**（对照 Swift `PetChatManager.swift` 全量；SSE 解析器可独立成内部静态类便于测试）
- [ ] **Step 4: 确认通过**
- [ ] **Step 5: Commit** — `feat(core): openai-compatible chat client with streaming`

---

### Task 3: Core RAG — 分块 + RAGManager 数据流 + LocalVectorStore

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Core/RAG/DocumentChunker.cs`
- Create: `DeskPet.Windows/src/DeskPet.Core/RAG/RAGManager.cs`
- Create: `DeskPet.Windows/src/DeskPet.Core/RAG/LocalVectorStore.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Rag/*Tests.cs`

**Interfaces:**
- Consumes: `IEmbeddingService`/`IVectorStore`/`VectorDocument`（T1）、`ISettingsService`（T1）。
- Produces:
  - `DocumentChunker`：文本分块（按段落/字符窗口 + 重叠，参照 Swift `RAGManager` 分块逻辑；`static IReadOnlyList<string> Chunk(string text, int chunkSize, int overlap)`）。
  - `RAGManager`：`Task ImportAsync(string sourcePath, string sourceName, ...)`（读文件→分块→`EmbedAsync`→`IVectorStore.UpsertAsync`）；`Task<IReadOnlyList<VectorSearchResult>> SearchAsync(string query, int topK, CancellationToken)`（query 也 embedding）；文档元数据（来源/块数/导入时间）管理 — 对应 Swift `RAGDocument`。
  - `LocalVectorStore : IVectorStore`：本地 JSON 持久化（`%APPDATA%/DeskPet/vectorstore.json` 语义 → Core 内用注入的存储路径或 `ISettingsService` 数据目录；JSON 存文档+向量）、余弦相似度检索（`Score`）、删除/清空；线程安全（lock）。对应 Swift `LocalVectorStore.swift`。
- 注意：Core 不得写死 `%APPDATA%` 路径 — 通过构造注入 `string dataDirectory`（应用层传 `Environment.GetFolderPath(ApplicationData)`），保持 Core 可测。

- [ ] **Step 1: 写失败测试**（分块边界/重叠；导入→检索 round-trip（Fake embedding 定长向量）；余弦相似度排序正确；持久化：写入→新实例读回→检索一致；删除/清空）
- [ ] **Step 2: 确认失败**
- [ ] **Step 3: 实现**（对照 Swift `RAGManager.swift`/`LocalVectorStore.swift`）
- [ ] **Step 4: 确认通过**
- [ ] **Step 5: Commit** — `feat(core): rag pipeline and local vector store`

---

### Task 4: 应用层 — 聊天/RAG 服务接线 + 设置持久化扩展

**Files:**
- Modify: `DeskPet.Windows/src/DeskPet.Windows/Services/JsonSettingsService.cs`（新配置项读写）
- Create: `DeskPet.Windows/src/DeskPet.Windows/Services/ChatServices.cs`（组装 `PetChatClient`（真实 `HttpClient`）+ `LocalVectorStore`（`%APPDATA%/DeskPet`）+ `RAGManager` 到 `AppServices`）
- Modify: `DeskPet.Windows/src/DeskPet.Windows/App.xaml.cs`（初始化）
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Services/JsonSettingsServiceChatTests.cs`（新配置默认值/读写/损坏容错）

**Interfaces:**
- Consumes: T1/T2/T3 的 Core 类型。
- Produces: `AppServices.Chat`/`AppServices.Rag`（静态实例）；`JsonSettingsService` 支持全部新配置。

- [ ] **Step 1: 失败测试**（JsonSettingsService 新配置默认值/round-trip）
- [ ] **Step 2: 确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: 确认通过**（build + 全量测试）
- [ ] **Step 5: Commit** — `feat(windows): wire chat and rag services`

---

### Task 5: 气泡 UI — 独立气泡窗 + 自言自语 + 对话输入

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Windows/Views/BubbleWindow.xaml` + `.xaml.cs`
- Create: `DeskPet.Windows/src/DeskPet.Windows/ViewModels/BubbleViewModel.cs`（CommunityToolkit.Mvvm 起步）
- Modify: `DeskPet.Windows/src/DeskPet.Windows/Views/PetWindow.xaml.cs`（气泡窗定位跟随、交互触发）

**Interfaces:**
- Consumes: `PetChatClient`/`IChatClient`（T2）、`ISettingsService`（T1）、`PetManager`（Phase 1）。
- Produces:
  - `BubbleWindow`：无边框小窗，位于宠物窗上方（随宠物移动，`PropertyChanged` 订阅 `WindowPosition` 同步）；内容：气泡样式（圆角、文字、打字指示）；点击气泡 → 输入框（回车发送）。
  - 自言自语：定时器（`BubbleIntervalSeconds`）→ 按当前动作随机台词（Swift `PetBubbleManager` phrases 字典移植为资源/代码）→ 气泡显示数秒消失。
  - 对话：发送 → `StreamChatAsync` → 流式显示（打字机效果）→ 历史持久化（`ChatHistoryCount` 上限）。
  - 对话时宠物动作联动（可选：说话时 happy 小动作）— Phase 2 仅气泡 + 可选动作，保持简单。
- 气泡不得阻塞宠物动画（独立窗口、不抢焦点、`ShowActivated=false` 可选）。

- [ ] **Step 1: 实现（UI 任务，构建 + 启动验证为主；台词选择逻辑可抽 Core 单测）**
- [ ] **Step 2: 构建 + 全量测试**
- [ ] **Step 3: 启动验证**（气泡出现/跟随/点击出输入框/流式显示；无头受限项如实标注，真机复验留给用户）
- [ ] **Step 4: Commit** — `feat(windows): chat bubble window`

---

### Task 6: RAG 接入设置 + 导入界面

**Files:**
- Modify: `DeskPet.Windows/src/DeskPet.Windows/Views/SettingsWindow.xaml` + `.xaml.cs`（Chat/RAG 页真实实现）
- Modify（如需）: `DeskPet.Windows/src/DeskPet.Windows/App.xaml.cs`

**Interfaces:**
- Consumes: `ISettingsService`（T1）、`RAGManager`（T3）、`PetChatClient`（T2）。
- Produces: 设置窗 Chat 页（Enabled/BaseUrl/APIKey/Model/SystemPrompt/Test Connection 按钮）+ RAG 页（EmbeddingModel/Import documents 文件选择/文档列表/删除/清空）— 对应 macOS 版设置页语义。

- [ ] **Step 1: 实现**
- [ ] **Step 2: 构建 + 测试 + 启动验证**
- [ ] **Step 3: Commit** — `feat(windows): chat and rag settings pages`

---

### Task 7: 对话注入 RAG 上下文 + 验收

**Files:**
- Modify: `DeskPet.Windows/src/DeskPet.Core/Chat/PetChatClient.cs` 或新增 `RagChatPipeline.cs`（对话前检索→注入 system/上下文，参照 Swift `PetChatManager` RAG 注入语义）
- Test: `.../Chat/RagChatPipelineTests.cs`

**Interfaces:**
- Consumes: `IChatClient`/`IVectorStore`（T1）、`RAGManager`（T3）。
- Produces: 对话时自动检索最相关 topK 片段注入请求（`RagEnabled` 时）。

- [ ] **Step 1: 失败测试**（注入：检索结果出现在请求体中；RagEnabled=false 不注入）
- [ ] **Step 2: 实现**
- [ ] **Step 3: 构建 + 测试**
- [ ] **Step 4: Commit** — `feat(core): inject rag context into chat`

---

### Task 8: Phase 2 验收 — 真机手动对照 + 终审

**Files:**
- Create: `docs/superpowers/plans/2026-08-19-windows-port-phase2-verification.md`

**验收矩阵：**
1. 设置页配置 Base URL/Key/Model（可用用户自己的 DeepSeek 等 OpenAI 兼容端点）
2. 点击气泡 → 输入 → 流式回复（打字机效果）；历史保留
3. 自言自语定时出现（按动作随机台词）
4. 导入文档 → RAG 对话引用（Embedding 模型可配）
5. 断网/错误配置 → 友好错误提示不崩溃
6. 气泡跟随宠物移动、不干扰拖拽/交互
7. 稳定性：长对话 + 导入后 5 分钟无卡死
- 执行：真机手动为主（无头受限项：真实 API 调用、流式观感）— 用户手动，记录结果。
- 发现的问题 → 修复轮。

- [ ] **Step 1: 用户手动验收**（给出清单，逐项记录）
- [ ] **Step 2: 修复发现问题**
- [ ] **Step 3: 全分支终审**（复用 Phase 0/1 流程）
- [ ] **Step 4: Commit 验收记录** — `docs: phase 2 verification record`

---

## Self-Review

**1. Spec coverage（§7 Phase 2）**
- LLM 流式对话（OpenAI 兼容）→ T2 ✓
- 气泡 UI + 自言自语 → T5 ✓
- RAG（导入→分块→向量→检索→对话注入）→ T3/T7 ✓
- 设置页配置 → T4/T6 ✓
- 历史持久化 → T2（ChatMessage 序列化）+ T5 ✓
- 音乐延后（用户指示）→ 不做 ✓

**2. Placeholder 扫描**：无 TBD/TODO；每个任务有文件/接口/测试/验证步骤；Swift 参照行号给出。

**3. 类型一致性**：`IChatClient`/`IEmbeddingService`/`IVectorStore`/`ChatMessage`/`VectorDocument` 在 T1 固定，T2-T7 消费；`ISettingsService` 扩展属性 T1 定义、T4 实现、T5/T6 消费；`RAGManager`/`LocalVectorStore` T3 定义、T6/T7 消费。跨任务签名一致。

**待跟进（Phase 3）**：面板/心情/清洁度 UI、DI 完善、多屏投放区、SMTC 音乐、DPI 坐标系修复（Phase 2 期间若用户机器 200% 缩放影响验收，插入一个小任务修复）。
