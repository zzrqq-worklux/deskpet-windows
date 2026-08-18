//
//  PetBubbleManager.swift
//  boringNotch
//
//  宠物头顶气泡：每隔几分钟根据当前动作随机说一句话；
//  点开气泡可输入对话（只显示当前对话，历史记录在设置页）。
//  气泡是独立于宠物窗口的浮动小窗，跟随宠物位置。
//

import AppKit
import Combine
import Defaults
import Foundation
import SwiftUI

/// 气泡当前展示的内容（一段"当前对话"）
struct PetBubble: Identifiable, Equatable {
    let id: UUID
    /// 来自用户的提问（气泡里小字显示）
    var userText: String?
    /// 宠物说的话
    var petText: String
    /// 是否正在流式接收回复（显示输入中动画）
    var isStreaming: Bool
    /// 自动消失时间（nil = 保持显示）
    var expiresAt: Date?
}

/// 对话气泡渲染接口（换肤入口）。
///
/// 想让气泡更好看时，实现本协议并替换 `PetBubbleManager.shared.rendering` 即可，
/// 聊天逻辑 / 定时说话 / LLM 调用全部复用：
///
///     struct 我的气泡: PetBubbleRendering {
///         var name: String { "我的风格" }
///         func makeRootView() -> AnyView {
///             AnyView(我的气泡视图())   // 内部用 PetBubbleManager.shared 读取状态/触发动作
///         }
///     }
///     // 初始化时：PetBubbleManager.shared.rendering = 我的气泡()
///
/// 视图内可用的状态与动作（都在 `PetBubbleManager.shared`）：
///   - `current`      当前气泡内容（userText / petText / isStreaming）
///   - `isInputActive` 是否打开输入框
///   - `send(_:)` / `openInput()` / `closeInput()` / `dismissBubble()`
/// 窗口尺寸由「设置 → Pet → 气泡大小」统一控制，样式内用给出的尺寸布局即可。
protocol PetBubbleRendering {
    /// 样式名（预留：后续可在设置里切换）
    var name: String { get }
    /// 返回包裹整个气泡的根视图（窗口创建时挂载一次，内部自行观察状态变化）
    func makeRootView() -> AnyView
}

/// 默认气泡样式（当前实现：圆角卡片 + 输入框 + 关闭按钮）
struct DefaultPetBubbleRendering: PetBubbleRendering {
    var name: String { "Default" }
    func makeRootView() -> AnyView {
        AnyView(DefaultPetBubbleView())
    }
}

/// 可接收键盘输入的透明气泡面板（非激活，仅在被点击输入时才成为 key window）
final class BubblePanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class PetBubbleManager: ObservableObject {
    static let shared = PetBubbleManager()

    // MARK: - 对外状态
    @Published private(set) var current: PetBubble?
    /// 是否打开了输入框
    @Published private(set) var isInputActive = false

    // MARK: - 动作 → 台词
    // 在这里改宠物说话的固定台词：数组里的每句话会随机抽一句。
    // .idle 是待机时自言自语；其他键对应宠物的动作（走/睡/开心/受伤…）。
    private let phrases: [PetAction: [String]] = [
        .idle: ["工作累了吗？", "你终于回来了！", "好无聊…陪我玩会儿嘛", "抱抱你～", "在想什么呢？"],
        .walk: ["出去溜达一下～", "活动活动筋骨～", "走走更健康！"],
        .happy: ["嘿嘿，今天心情超好！", "耶！开心！", "你最好啦～"],
        .hurt: ["呜…轻一点嘛", "你戳我干嘛呀…"],
        .yawn: ["哈啊…有点困了", "想睡觉了…"],
        .sleep: ["Zzz…", "呼噜…"],
        .home: ["Zzz… 睡大觉"],
        .music: ["♪(´▽｀) 跟着音乐摇摆～", "这歌真好听！"],
        .drag: ["哇——飞起来啦！"],
        .fall: ["呜哇——！"],
    ]

    /// 定时说话的间隔（秒）：待机时的自言自语频率（由设置控制，带 ±25% 随机）
    private var speechInterval: ClosedRange<Double> {
        let base = max(15, Defaults[.petBubbleSpeechInterval])
        return (base * 0.75)...(base * 1.5)
    }
    private var nextSpeechAt = Date()
    /// 动作触发说话的冷却时间（秒）：随设置间隔缩放，防止连续动作刷屏
    private var actionSpeechCooldown: TimeInterval {
        max(20, Defaults[.petBubbleSpeechInterval] * 0.7)
    }
    /// 动作触发说话的时长（秒）
    private let actionSpeechDuration: TimeInterval = 8
    /// 上次动作触发说话的时间
    private var lastActionSpeechAt = Date.distantPast
    /// 只有"做动作"时才开口（待机自言自语交给定时器）
    private let speakableActions: Set<PetAction> = [.walk, .sleep, .yawn, .happy, .hurt, .fall, .drag, .music]

    private var bubbleWindow: BubblePanel?
    private var speechTimer: Timer?
    private var hideTask: Task<Void, Never>?
    private var cancellables: Set<AnyCancellable> = []
    private var bubbleSizeObservation: Defaults.Observation?

    /// 当前气泡大小（由设置驱动）
    var bubbleSize: CGSize {
        Defaults[.petBubbleSize].size
    }

    /// 气泡渲染器（换肤接口）：默认用当前实现，后续可替换成其他风格
    var rendering: any PetBubbleRendering = DefaultPetBubbleRendering()

    private init() {
        // 跟随宠物位置移动（读真实窗口坐标，拖拽期间也准确）
        PetManager.shared.$windowPosition
            .compactMap { $0 }
            .sink { [weak self] _ in self?.repositionWindow() }
            .store(in: &cancellables)
        // 拖拽时宠物窗口被直接搬动/提层，气泡不跟随：先隐藏，松手后再恢复
        PetManager.shared.$isDragging
            .sink { [weak self] dragging in
                if dragging {
                    self?.hideWindow()
                } else if self?.current != nil {
                    self?.showWindow()
                }
            }
            .store(in: &cancellables)
        // 回窝时隐藏气泡
        PetManager.shared.$lifeState
            .sink { [weak self] state in
                if state == .atHome { self?.dismissBubble() }
            }
            .store(in: &cancellables)
        // 宠物开始做动作时（走动/睡觉/开心/受伤/掉落…）触发说话，带冷却
        PetManager.shared.$action
            .dropFirst()
            .sink { [weak self] action in self?.actionChanged(action) }
            .store(in: &cancellables)
        // 聊天消息变化 → 气泡实时反映流式回复 / 最终回复
        PetChatManager.shared.$messages
            .sink { [weak self] messages in self?.chatMessagesChanged(messages) }
            .store(in: &cancellables)
        // 聊天出错 → 气泡显示错误（仅在流式中途失败时）
        PetChatManager.shared.$lastError
            .compactMap { $0 }
            .sink { [weak self] error in
                guard let self, self.current?.isStreaming == true else { return }
                self.current = PetBubble(
                    id: UUID(),
                    userText: self.current?.userText,
                    petText: error,
                    isStreaming: false,
                    expiresAt: Date().addingTimeInterval(12)
                )
                self.scheduleHide(after: 12)
            }
            .store(in: &cancellables)

        startSpeechTimer()

        // 设置里调整气泡大小时：重设窗口尺寸并重新定位
        bubbleSizeObservation = Defaults.observe(.petBubbleSize) { [weak self] _ in
            Task { @MainActor in
                self?.resizeBubbleWindow()
            }
        }
    }

    // MARK: - 交互入口

    func toggleInput() {
        isInputActive ? closeInput() : openInput()
    }

    func openInput() {
        isInputActive = true
        hideTask?.cancel()
        let configured = PetChatManager.shared.isConfigured
        current = PetBubble(
            id: UUID(),
            userText: nil,
            petText: configured ? "想聊点什么？" : "还没配置 AI，去 设置 → Chat 填一下就能聊天啦",
            isStreaming: false,
            expiresAt: nil
        )
        showWindow()
    }

    func closeInput() {
        isInputActive = false
        if current != nil {
            scheduleHide(after: 8)
        }
    }

    func dismissBubble() {
        hideTask?.cancel()
        current = nil
        isInputActive = false
        hideWindow()
    }

    /// 气泡里发送一条消息（走后端 LLM）
    func send(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        guard PetChatManager.shared.isConfigured else {
            // 退出输入态：显示提示 + 关闭按钮，8 秒后自动消失
            isInputActive = false
            current = PetBubble(
                id: UUID(),
                userText: trimmed,
                petText: "还没配置 AI 连接，去 设置 → Chat 填 Base URL / API Key 吧",
                isStreaming: false,
                expiresAt: Date().addingTimeInterval(8)
            )
            scheduleHide(after: 8)
            return
        }
        PetChatManager.shared.clearLastErrorForBubble()
        isInputActive = false
        current = PetBubble(
            id: UUID(),
            userText: trimmed,
            petText: "…",
            isStreaming: true,
            expiresAt: nil
        )
        PetManager.shared.poke()
        Task { await PetChatManager.shared.send(trimmed) }
    }

    // MARK: - 定时说话

    private func startSpeechTimer() {
        speechTimer?.invalidate()
        speechTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.speechTick() }
        }
        RunLoop.main.add(speechTimer!, forMode: .common)
        nextSpeechAt = Date().addingTimeInterval(20)
    }

    private func speechTick() {
        // 正在输入 / 正在聊天时不自动说话
        guard !isInputActive, current?.isStreaming != true, current?.expiresAt == nil else { return }
        guard PetManager.shared.lifeState == .roaming else { return }

        let now = Date()
        // 过期气泡自动消失
        if let current, let expiresAt = current.expiresAt, now >= expiresAt {
            self.current = nil
            hideWindow()
        }
        // 到点随机说一句（按当前动作）
        if now >= nextSpeechAt {
            nextSpeechAt = now.addingTimeInterval(Double.random(in: speechInterval))
            if let phrase = phrases[PetManager.shared.action]?.randomElement() {
                showPhrase(phrase)
            }
        }
    }

    private func showPhrase(_ text: String) {
        current = PetBubble(
            id: UUID(),
            userText: nil,
            petText: text,
            isStreaming: false,
            expiresAt: Date().addingTimeInterval(actionSpeechDuration)
        )
        showWindow()
        scheduleHide(after: actionSpeechDuration)
    }

    /// 宠物切换到某个"动作"时说话（带冷却，避免连续动作刷屏）
    private func actionChanged(_ action: PetAction) {
        guard speakableActions.contains(action) else { return }
        guard PetManager.shared.lifeState == .roaming, !isInputActive, current?.isStreaming != true else { return }
        let now = Date()
        guard now.timeIntervalSince(lastActionSpeechAt) >= actionSpeechCooldown else { return }
        lastActionSpeechAt = now
        if let phrase = phrases[action]?.randomElement() {
            showPhrase(phrase)
        }
    }

    // MARK: - 聊天消息 → 气泡

    private func chatMessagesChanged(_ messages: [ChatMessage]) {
        guard let last = messages.last, last.role == .assistant else { return }
        guard let user = messages.dropLast().last(where: { $0.role == .user }) else { return }

        // 用 assistant 消息 id 作为气泡 id → 流式期间 id 稳定，不闪烁
        let bubble = PetBubble(
            id: last.id,
            userText: user.content,
            petText: last.content.isEmpty ? (last.isStreaming ? "…" : "（空回复）") : last.content,
            isStreaming: last.isStreaming,
            expiresAt: last.isStreaming ? nil : Date().addingTimeInterval(25)
        )
        current = bubble
        if last.isStreaming {
            showWindow()
        } else {
            scheduleHide(after: 25)
        }
    }

    // MARK: - 气泡窗口

    private func showWindow() {
        if bubbleWindow == nil {
            let size = bubbleSize
            let panel = BubblePanel(
                contentRect: NSRect(x: 0, y: 0, width: size.width, height: size.height),
                styleMask: [.borderless, .nonactivatingPanel],
                backing: .buffered,
                defer: false
            )
            panel.isFloatingPanel = true
            panel.level = .floating + 1
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.hasShadow = false
            panel.isMovable = false
            panel.hidesOnDeactivate = false
            panel.becomesKeyOnlyIfNeeded = true
            panel.ignoresMouseEvents = false
            panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
            panel.contentView = NSHostingView(rootView: rendering.makeRootView())
            bubbleWindow = panel
        }
        bubbleWindow?.orderFrontRegardless()
        repositionWindow()
    }

    private func hideWindow() {
        bubbleWindow?.orderOut(nil)
    }

    /// 设置改变气泡大小时调用：更新窗口尺寸并重新定位
    private func resizeBubbleWindow() {
        guard let window = bubbleWindow else { return }
        var frame = window.frame
        frame.size = bubbleSize
        window.setFrame(frame, display: true)
        repositionWindow()
    }

    private func repositionWindow() {
        guard let window = bubbleWindow else { return }
        // 用宠物窗口的真实坐标（含拖拽时的直接搬动），比 windowPosition 更准
        let petOrigin = PetWindowController.shared.currentOrigin ?? PetManager.shared.windowPosition
        guard let petOrigin else { return }
        let petSize = PetManager.shared.windowSize
        var origin = NSPoint(
            x: petOrigin.x + petSize.width / 2 - window.frame.width / 2,
            y: petOrigin.y + petSize.height + 4
        )
        // 屏幕内约束
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(petOrigin) }) ?? NSScreen.main {
            let visible = screen.visibleFrame
            origin.x = min(max(origin.x, visible.minX + 4), visible.maxX - 4 - window.frame.width)
            origin.y = min(max(origin.y, visible.minY + 4), visible.maxY - 4 - window.frame.height)
        }
        window.setFrameOrigin(origin)
    }

    private func scheduleHide(after seconds: TimeInterval) {
        hideTask?.cancel()
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            guard let self, !Task.isCancelled else { return }
            if self.isInputActive { return }
            self.current = nil
            self.hideWindow()
        }
    }

    /// 应用退出 / 宠物隐藏时清理
    func shutdown() {
        speechTimer?.invalidate()
        speechTimer = nil
        hideTask?.cancel()
        bubbleWindow?.orderOut(nil)
        bubbleWindow = nil
    }
}