//
//  PetBubbleView.swift
//  boringNotch
//
//  宠物头顶的气泡内容：当前对话（用户小字 + 宠物回复）+ 输入框。
//

import Defaults
import SwiftUI

struct DefaultPetBubbleView: View {
    @ObservedObject private var bubble = PetBubbleManager.shared
    @Default(.petBubbleSize) private var bubbleSize: PetBubbleSize
    @State private var input = ""
    @FocusState private var inputFocused: Bool

    /// 气泡窗口尺寸（设置可调）
    private var size: CGSize { bubbleSize.size }
    /// 卡片高度（留出下方尾巴）
    private var cardHeight: CGFloat { size.height - 10 }

    var body: some View {
        ZStack(alignment: .topLeading) {
            bubbleCard
            // 尾巴：指向宠物
            TailShape()
                .fill(Color(NSColor.windowBackgroundColor).opacity(0.98))
                .frame(width: 16, height: 10)
                .offset(x: size.width / 2 - 8, y: size.height - 2)
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

    private var bubbleCard: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let current = bubble.current {
                if let userText = current.userText, !userText.isEmpty {
                    Text(userText)
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .trailing)
                        .lineLimit(2)
                }

                if current.isStreaming {
                    HStack(spacing: 4) {
                        ForEach(0..<3, id: \.self) { index in
                            Circle()
                                .frame(width: 5, height: 5)
                                .foregroundStyle(.secondary)
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
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .lineLimit(3)
                }
            } else {
                Text("…")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
            }

            Spacer(minLength: 0)

            if bubble.isInputActive {
                inputBar
            } else if bubble.current != nil {
                actionRow
            }
        }
        .padding(10)
        .frame(width: size.width, height: cardHeight, alignment: .top)
        .background(
            Color(NSColor.windowBackgroundColor).opacity(0.98),
            in: RoundedRectangle(cornerRadius: 14, style: .continuous)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(.white.opacity(0.15), lineWidth: 1)
        )
        .shadow(color: .black.opacity(0.35), radius: 12, y: 6)
    }

    private var inputBar: some View {
        HStack(spacing: 6) {
            TextField("说点什么…", text: $input)
                .textFieldStyle(.plain)
                .font(.system(size: 11))
                .focused($inputFocused)
                .onSubmit { submit() }
            Button {
                submit()
            } label: {
                Image(systemName: "arrow.up.circle.fill")
                    .font(.system(size: 16))
            }
            .buttonStyle(.plain)
            .disabled(input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

            // 输入态也能主动关闭气泡
            Button {
                bubble.dismissBubble()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 14))
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("关闭气泡")
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(Color(NSColor.controlBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
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

            Spacer()

            Button {
                bubble.dismissBubble()
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
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

/// 气泡尾部小三角
struct TailShape: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.midX, y: rect.maxY))
        path.closeSubpath()
        return path
    }
}