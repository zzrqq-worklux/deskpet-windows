//
//  PetChatManager.swift
//  boringNotch
//
//  桌宠对话中枢：连接任意 OpenAI 兼容 API（OpenAI / DeepSeek / Moonshot / Ollama / LM Studio…），
//  维护会话历史（持久化到 Defaults），流式接收回复，并在发送前预留 RAG 检索入口。
//

import Combine
import Defaults
import Foundation

/// 单条聊天消息
struct ChatMessage: Identifiable, Codable, Equatable, Defaults.Serializable {
    enum Role: String, Codable {
        case user
        case assistant
        case system
    }

    let id: UUID
    var role: Role
    var content: String
    /// 是否仍在流式接收中（UI 用于展示"正在输入"）
    var isStreaming: Bool

    init(id: UUID = UUID(), role: Role, content: String, isStreaming: Bool = false) {
        self.id = id
        self.role = role
        self.content = content
        self.isStreaming = isStreaming
    }
}

/// 对话相关错误
enum PetChatError: LocalizedError {
    case notEnabled
    case notConfigured(String)
    case invalidURL
    case invalidResponse
    case httpError(Int, String)
    case emptyResponse

    var errorDescription: String? {
        switch self {
        case .notEnabled:
            return "请在 设置 → Chat 中启用对话并填写 API 信息"
        case .notConfigured(let message):
            return message
        case .invalidURL:
            return "Base URL 无效"
        case .invalidResponse:
            return "服务返回了无法解析的响应"
        case .httpError(let code, let body):
            return "请求失败（HTTP \(code)）：\(body)"
        case .emptyResponse:
            return "模型没有返回内容"
        }
    }
}

@MainActor
final class PetChatManager: ObservableObject {
    static let shared = PetChatManager()

    // MARK: - 对外状态
    @Published private(set) var messages: [ChatMessage] = []
    @Published private(set) var isResponding = false
    @Published private(set) var lastError: String?

    /// 是否已满足对话条件（开关开启 + 已填模型）
    var isConfigured: Bool {
        Defaults[.petChatEnabled] && !Defaults[.petChatModel].isEmpty
    }

    /// 是否已填 API Key（Ollama 等本地服务可留空）
    var hasAPIKey: Bool {
        !Defaults[.petChatAPIKey].isEmpty
    }

    private init() {
        messages = Defaults[.petChatHistory]
    }

    // MARK: - 发送消息（流式）

    func send(_ text: String) async {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }

        guard Defaults[.petChatEnabled] else {
            lastError = PetChatError.notEnabled.errorDescription
            return
        }
        lastError = nil

        // 追加用户消息 + 空的 assistant 占位（流式填充）
        messages.append(ChatMessage(role: .user, content: trimmed))
        let assistantID = UUID()
        messages.append(ChatMessage(id: assistantID, role: .assistant, content: "", isStreaming: true))
        isResponding = true

        do {
            // RAG：向量库已就绪时，先嵌入查询再检索相关文档，非空时注入上下文
            var ragResults: [VectorSearchResult] = []
            if VectorStoreManager.shared.isConfigured {
                if let queryEmbedding = try? await embed([trimmed]).first {
                    ragResults = await VectorStoreManager.shared.search(query: trimmed, embedding: queryEmbedding)
                }
            }
            let ragContext = VectorStoreManager.shared.contextPrompt(from: ragResults)

            let request = try makeRequest(history: messages, userText: trimmed, ragContext: ragContext, stream: true)
            let (bytes, response) = try await URLSession.shared.bytes(for: request)

            guard let http = response as? HTTPURLResponse else {
                throw PetChatError.invalidResponse
            }
            guard http.statusCode == 200 else {
                let errorBody = try await Self.collectBody(from: bytes)
                throw PetChatError.httpError(http.statusCode, errorBody)
            }

            // 逐行解析 SSE（data: {...}），同时累积原始 body 供非流式兜底
            var content = ""
            var rawBody = ""
            var sawDataEvent = false
            for try await line in bytes.lines {
                rawBody += line + "\n"
                let trimmedLine = line.trimmingCharacters(in: .whitespaces)
                guard trimmedLine.hasPrefix("data:") else { continue }
                let payload = String(trimmedLine.dropFirst("data:".count))
                    .trimmingCharacters(in: .whitespaces)
                guard !payload.isEmpty else { continue }
                if payload == "[DONE]" {
                    sawDataEvent = true
                    break
                }
                sawDataEvent = true
                if let piece = Self.parseStreamDelta(payload) {
                    content += piece
                    updateAssistantMessage(id: assistantID, content: content)
                }
            }

            // 兜底：某些兼容服务忽略 stream 参数直接返回完整 JSON
            if !sawDataEvent, let full = Self.parseNonStreamContent(rawBody) {
                content = full
            }

            guard !content.isEmpty else {
                throw PetChatError.emptyResponse
            }
            finishAssistantMessage(id: assistantID, content: content)
        } catch {
            // 失败时移除空的 assistant 占位，避免留下气泡
            messages.removeAll { $0.id == assistantID }
            lastError = error.localizedDescription
        }

        isResponding = false
        persist()
    }

    // MARK: - 测试连接（非流式，一次性请求）

    func testConnection() async throws -> String {
        let request = try makeRequest(history: [], userText: "请只回复两个字符：OK", ragContext: nil, stream: false)
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw PetChatError.invalidResponse }
        guard http.statusCode == 200 else {
            let body = String(data: data, encoding: .utf8) ?? ""
            throw PetChatError.httpError(http.statusCode, String(body.prefix(300)))
        }
        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let choices = json["choices"] as? [[String: Any]],
              let message = choices.first?["message"] as? [String: Any],
              let content = message["content"] as? String,
              !content.isEmpty
        else {
            throw PetChatError.emptyResponse
        }
        return content
    }

    // MARK: - 历史管理

    func clearHistory() {
        messages = []
        lastError = nil
        persist()
    }

    /// 清空最近一次错误（气泡发送前调用，避免残留旧错误）
    func clearLastErrorForBubble() {
        lastError = nil
    }

    // MARK: - Embedding（RAG 用）

    /// 批量生成文本向量（OpenAI 兼容 /embeddings 接口，自动分批）
    func embed(_ texts: [String]) async throws -> [[Double]] {
        let nonEmpty = texts.filter { !$0.isEmpty }
        guard !nonEmpty.isEmpty else { return [] }
        let model = Defaults[.petRAGEmbeddingModel].trimmingCharacters(in: .whitespacesAndNewlines)
        guard !model.isEmpty else {
            throw PetChatError.notConfigured("请先在 设置 → RAG 中填写 Embedding 模型名")
        }

        var embeddings: [[Double]] = []
        for batch in nonEmpty.batched(by: 32) {
            var request = URLRequest(url: try Self.endpointURL(from: Defaults[.petChatBaseURL], path: "embeddings"))
            request.httpMethod = "POST"
            request.timeoutInterval = 90
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            let apiKey = Defaults[.petChatAPIKey].trimmingCharacters(in: .whitespacesAndNewlines)
            if !apiKey.isEmpty {
                request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
            }
            let body: [String: Any] = [
                "model": model,
                "input": batch,
            ]
            request.httpBody = try JSONSerialization.data(withJSONObject: body)

            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw PetChatError.invalidResponse }
            guard http.statusCode == 200 else {
                let body = String(data: data, encoding: .utf8) ?? ""
                throw PetChatError.httpError(http.statusCode, String(body.prefix(300)))
            }
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let data = json["data"] as? [[String: Any]]
            else {
                throw PetChatError.invalidResponse
            }
            // 按 index 排序，避免服务端乱序
            let sorted = data.sorted { ($0["index"] as? Int ?? 0) < ($1["index"] as? Int ?? 0) }
            let batchVectors: [[Double]] = sorted.compactMap { entry in
                entry["embedding"] as? [Double]
            }
            guard batchVectors.count == batch.count else { throw PetChatError.invalidResponse }
            embeddings.append(contentsOf: batchVectors)
        }
        return embeddings
    }

    // MARK: - 请求构建

    private func makeRequest(history: [ChatMessage], userText: String, ragContext: String?, stream: Bool) throws -> URLRequest {
        var apiMessages: [[String: String]] = []

        // 宠物人设
        let systemPrompt = Defaults[.petChatSystemPrompt]
        if !systemPrompt.isEmpty {
            apiMessages.append(["role": "system", "content": systemPrompt])
        }
        // RAG 检索到的参考资料作为一条 system 消息注入
        if let ragContext, !ragContext.isEmpty {
            apiMessages.append(["role": "system", "content": ragContext])
        }
        // 裁剪历史，只保留最近 N 条
        let maxHistory = max(1, Defaults[.petChatMaxHistory])
        var historyToSend = Array(history.suffix(maxHistory))
        // 确保本次用户输入一定在最后（历史被裁剪时也能命中）
        if historyToSend.last?.content != userText {
            historyToSend.append(ChatMessage(role: .user, content: userText))
        }
        for msg in historyToSend where msg.role != .system {
            apiMessages.append(["role": msg.role.rawValue, "content": msg.content])
        }

        var request = URLRequest(url: try Self.endpointURL(from: Defaults[.petChatBaseURL], path: "chat/completions"))
        request.httpMethod = "POST"
        request.timeoutInterval = 90
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let apiKey = Defaults[.petChatAPIKey].trimmingCharacters(in: .whitespacesAndNewlines)
        if !apiKey.isEmpty {
            request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        }
        let body: [String: Any] = [
            "model": Defaults[.petChatModel],
            "messages": apiMessages,
            "stream": stream,
            "temperature": 0.7,
        ]
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return request
    }

    /// 合成 API 完整 URL（兼容用户只填域名、/v1 或完整 endpoint），
    /// path 形如 "chat/completions" / "embeddings"
    static func endpointURL(from base: String, path: String) throws -> URL {
        var trimmed = base.trimmingCharacters(in: .whitespacesAndNewlines)
        while trimmed.hasSuffix("/") {
            trimmed = String(trimmed.dropLast())
        }
        if trimmed.lowercased().hasSuffix("/\(path)") {
            guard let url = URL(string: trimmed) else { throw PetChatError.invalidURL }
            return url
        }
        var urlString = trimmed
        if !trimmed.lowercased().hasSuffix("/v1") {
            urlString = trimmed + "/v1"
        }
        urlString += "/\(path)"
        guard let url = URL(string: urlString) else { throw PetChatError.invalidURL }
        return url
    }

    // MARK: - 消息更新辅助

    private func updateAssistantMessage(id: UUID, content: String) {
        if let index = messages.firstIndex(where: { $0.id == id }) {
            messages[index].content = content
        }
    }

    private func finishAssistantMessage(id: UUID, content: String) {
        if let index = messages.firstIndex(where: { $0.id == id }) {
            messages[index].content = content
            messages[index].isStreaming = false
        }
    }

    private func persist() {
        Defaults[.petChatHistory] = Array(messages.suffix(max(20, Defaults[.petChatMaxHistory])))
    }

    // MARK: - SSE / JSON 解析

    /// 解析一条 SSE data 载荷里的增量内容
    private static func parseStreamDelta(_ payload: String) -> String? {
        guard let data = payload.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let choices = json["choices"] as? [[String: Any]],
              let delta = choices.first?["delta"] as? [String: Any],
              let piece = delta["content"] as? String
        else {
            return nil
        }
        return piece.isEmpty ? nil : piece
    }

    /// 非流式 JSON 兜底：从完整 body 提取 content
    private static func parseNonStreamContent(_ rawBody: String) -> String? {
        guard let data = rawBody.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let choices = json["choices"] as? [[String: Any]],
              let message = choices.first?["message"] as? [String: Any],
              let content = message["content"] as? String,
              !content.isEmpty
        else {
            return nil
        }
        return content
    }

    /// 读取错误响应 body（截断）
    private static func collectBody(from bytes: URLSession.AsyncBytes) async throws -> String {
        var body = ""
        for try await line in bytes.lines {
            body += line + " "
            if body.count > 300 { break }
        }
        return String(body.prefix(300))
    }
}
