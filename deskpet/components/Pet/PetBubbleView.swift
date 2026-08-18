//
//  PetBubbleView.swift
//  boringNotch
//
//  宠物头顶的气泡内容：当前对话（用户小字 + 宠物回复）+ 输入框。
//  皮肤支持：默认（圆角卡片）或位图鲸鱼气泡皮肤。
//

import Defaults
import SwiftUI

struct DefaultPetBubbleView: View {
    @ObservedObject private var bubble = PetBubbleManager.shared
    @Default(.petBubbleSkin) private var bubbleSkin: PetBubbleSkin
    @State private var input = ""
    @FocusState private var inputFocused: Bool

    /// 气泡窗口尺寸（设置可调）
    private var size: CGSize { bubble.bubbleSize }

    /// 是否为深色气泡底（夜款等），用于决定文字/控件颜色
    private var isDarkSkin: Bool {
        bubbleSkin == .whaleNight
    }

    /// 主体文字颜色：深色底用浅色，浅色底用深色
    private var primaryText: Color {
        isDarkSkin ? .white : .primary
    }

    /// 次要文字颜色（用户问题、提示）
    private var secondaryText: Color {
        isDarkSkin ? .white.opacity(0.75) : .secondary
    }

    var body: some View {
        ZStack(alignment: .topLeading) {
            bubbleCard
            // 测量层：同宽度、无高度约束，让 SwiftUI 算出内容的自然高度并上报
            // （窗口高度跟随内容，聊天按钮永远在气泡底部内）
            contentArea
                .padding(.horizontal, 16)
                .padding(.vertical, 14)
                .fixedSize(horizontal: false, vertical: true)
                .frame(width: size.width)
                .hidden()
                .background(
                    GeometryReader { geo in
                        Color.clear
                            .onAppear { bubble.reportContentHeight(geo.size.height) }
                            .onChange(of: geo.size.height) { _, newH in
                                bubble.reportContentHeight(newH)
                            }
                    }
                )
        }
        .frame(width: size.width, height: size.height, alignment: .top)
        .onAppear { inputFocused = bubble.isInputActive }
        .onChange(of: bubble.isInputActive) { _, active in
            if active {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
                    inputFocused = true
                }
            }
        }
    }

    /// 气泡背景（默认代码绘制 / 或位图皮肤）
    @ViewBuilder
    private var bubbleBackground: some View {
        if let imageName = bubbleSkin.imageName, let image = NSImage(named: imageName) {
            BubbleSkinImage(image: image, size: size)
        } else {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Color(NSColor.windowBackgroundColor).opacity(0.98))
                .overlay(
                    RoundedRectangle(cornerRadius: 14, style: .continuous)
                        .strokeBorder(.white.opacity(0.15), lineWidth: 1)
                )
        }
    }

    /// 气泡内容（文字 + 输入框/按钮），皮肤只负责背景
    private var contentArea: some View {
        VStack(alignment: .leading, spacing: 6) {
            ScrollView(.vertical, showsIndicators: true) {
                messageArea
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            // 固定在底部，不参与消息滚动，永远可见。
            if bubble.isInputActive {
                inputBar
            } else if bubble.current != nil {
                actionRow
            }
        }
    }

    private var messageArea: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let current = bubble.current {
                if let userText = current.userText, !userText.isEmpty {
                    Text(userText)
                        .font(.system(size: 10))
                        .foregroundStyle(secondaryText)
                        .frame(maxWidth: .infinity, alignment: .trailing)
                        .lineLimit(2)
                }

                if current.isStreaming {
                    HStack(spacing: 4) {
                        ForEach(0..<3, id: \.self) { index in
                            Circle()
                                .frame(width: 5, height: 5)
                                .foregroundStyle(secondaryText)
                                .opacity(0.4)
                                .animation(
                                    .easeInOut(duration: 0.6).repeatForever(autoreverses: true)
                                        .delay(Double(index) * 0.15),
                                    value: current.isStreaming
                                )
                        }
                        Spacer()
                    }
                    .frame(height: 18)
                } else {
                    Text(current.petText)
                        .font(.system(size: 12))
                        .foregroundStyle(primaryText)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .fixedSize(horizontal: false, vertical: true)
                }
            } else {
                Text("…")
                    .font(.system(size: 12))
                    .foregroundStyle(secondaryText)
            }

        }
    }

    private var bubbleCard: some View {
        contentArea
        .padding(.horizontal, 16)
        .padding(.top, 14)
        .padding(.bottom, 14)
        .frame(width: size.width, height: size.height, alignment: .top)
        .background(bubbleBackground)
        .shadow(color: .black.opacity(0.35), radius: 12, y: 6)
    }

    private var inputBar: some View {
        HStack(spacing: 6) {
            TextField("说点什么…", text: $input)
                .textFieldStyle(.plain)
                .font(.system(size: 11))
                .foregroundStyle(primaryText)
                .focused($inputFocused)
                .onSubmit { submit() }
            Button {
                submit()
            } label: {
                Image(systemName: "arrow.up.circle.fill")
                    .font(.system(size: 16))
                    .foregroundStyle(isDarkSkin ? .white : .accentColor)
            }
            .buttonStyle(.plain)
            .disabled(input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

            // 输入态也能主动关闭气泡
            Button {
                bubble.dismissBubble()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 14))
                    .foregroundStyle(secondaryText)
            }
            .buttonStyle(.plain)
            .help("关闭气泡")
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(
            (isDarkSkin ? Color.white.opacity(0.18) : Color(NSColor.controlBackgroundColor).opacity(0.85)),
            in: RoundedRectangle(cornerRadius: 8)
        )
    }

    private var actionRow: some View {
        HStack(spacing: 8) {
            Button {
                bubble.openInput()
            } label: {
                Label("聊天", systemImage: "bubble.left.and.bubble.right.fill")
                    .font(.system(size: 10))
            }
            .buttonStyle(.bordered)
            .controlSize(.mini)
            .tint(isDarkSkin ? .white : .accentColor)

            Spacer()

            Button {
                bubble.dismissBubble()
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 9))
                    .foregroundStyle(secondaryText)
            }
            .buttonStyle(.plain)
            .help("关闭气泡")
        }
    }

    private func submit() {
        let text = input
        input = ""
        inputFocused = false
        bubble.send(text)
    }
}

/// 用图片做气泡背景。气泡尺寸已按图片宽高比计算，用 fit 完整显示整张图。
struct BubbleSkinImage: View {
    let image: NSImage
    let size: CGSize

    var body: some View {
        Image(nsImage: image)
            // 动态高度由对话内容决定，必须铺满整个气泡窗口；
            // 使用 stretch 避免图片底边与文字区域之间出现矩形边界。
            .resizable(resizingMode: .stretch)
            .interpolation(.high)
            .frame(width: size.width, height: size.height)
    }
}
