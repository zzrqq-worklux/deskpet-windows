//
//  VectorStoreManager.swift
//  boringNotch
//
//  向量库注册中心 + RAG 检索入口。
//  当前内置「本地 JSON 向量库」（LocalVectorStore）作为默认后端；
//  后续也可追加 sqlite-vec / Core ML embeddings / 远程向量服务等实现
//  VectorStoreProviding 后注册到这里，PetChatManager 会自动使用。
//

import Combine
import Foundation

@MainActor
final class VectorStoreManager: ObservableObject {
    static let shared = VectorStoreManager()

    /// 已注册的向量库后端
    @Published private(set) var providers: [any VectorStoreProviding] = []

    /// 内置本地向量库
    var localStore: LocalVectorStore {
        LocalVectorStore.shared
    }

    /// RAG 是否可用（至少一个后端已就绪）
    var isConfigured: Bool {
        providers.contains { $0.isConfigured }
    }

    /// 当前已存分块总数
    var totalChunkCount: Int {
        localStore.chunks.count
    }

    private init() {
        providers.append(LocalVectorStore.shared)
    }

    /// 统一检索入口：query 已由调用方嵌入，按优先级尝试已注册后端。
    func search(query: String, embedding: [Double]?, topK: Int = 3) async -> [VectorSearchResult] {
        guard isConfigured else { return [] }
        for provider in providers where provider.isConfigured {
            if let results = try? await provider.search(query: query, embedding: embedding, topK: topK) {
                return results
            }
        }
        return []
    }

    // MARK: - 文档管理（给 RAGManager 用）

    func upsertChunks(_ documents: [VectorDocument]) async throws {
        try await localStore.upsert(documents)
    }

    func deleteChunks(sourceName: String) async throws {
        try await localStore.delete(sourceName: sourceName)
    }

    func clearAllChunks() async throws {
        try await localStore.clear()
    }

    /// 预留：把检索结果格式化成可注入 system 上下文的参考文本。
    func contextPrompt(from results: [VectorSearchResult], maxCharacters: Int = 2000) -> String? {
        guard !results.isEmpty else { return nil }
        var remaining = maxCharacters
        var lines: [String] = []
        for result in results {
            let line = "• \(result.text.replacingOccurrences(of: "\n", with: " "))"
            if line.count > remaining { break }
            lines.append(line)
            remaining -= line.count + 1
        }
        guard !lines.isEmpty else { return nil }
        return "以下是宠物主人提供的参考资料（可能与问题相关，也可能无关，请按需采用，不要编造）：\n"
            + lines.joined(separator: "\n")
    }
}
