//
//  VectorStoreProviding.swift
//  boringNotch
//
//  向量库统一接口（预留）：为后续 RAG 能力定义存储/检索契约。
//  当前应用内没有注册任何实现，VectorStoreManager 会返回空结果；
//  后续接入 sqlite-vec / Core ML embeddings / 远程向量服务时，
//  实现本协议并注册到 VectorStoreManager.shared.providers 即可，
//  PetChatManager 会自动把检索结果拼进对话上下文（无需改动调用方）。
//

import Foundation

/// 向量库中的一条文档（文本块 + 可选元数据 + 可选 embedding）
struct VectorDocument: Identifiable, Codable, Equatable {
    let id: String
    var text: String
    var metadata: [String: String] = [:]
    /// 预留：存储该文本的 embedding 向量（由嵌入模型生成，可为空，检索时再实时计算）
    var embedding: [Double]?
}

/// 检索结果
struct VectorSearchResult: Identifiable, Equatable {
    let id: String
    let text: String
    let metadata: [String: String]
    /// 相似度分数（0-1，越高越相关；由具体实现定义）
    let score: Double
}

/// 向量库后端需要实现的能力
protocol VectorStoreProviding {
    /// 后端展示名（设置页展示用）
    var name: String { get }
    /// 是否已就绪（数据库存在 / 服务可用）
    var isConfigured: Bool { get }

    /// 批量写入 / 更新文档
    func upsert(_ documents: [VectorDocument]) async throws

    /// 语义检索：传入查询文本和/或 query embedding，返回最相关的 topK 条
    func search(query: String, embedding: [Double]?, topK: Int) async throws -> [VectorSearchResult]

    /// 删除指定 id 的文档
    func delete(ids: [String]) async throws

    /// 清空所有文档
    func clear() async throws
}

/// 便捷扩展：embedding 未接入时按空实现处理
extension VectorStoreProviding {
    var embedding: [Double]? { nil }
}

extension Array {
    /// 按固定大小分批（embedding 批量请求用）
    func batched(by size: Int) -> [[Element]] {
        guard size > 0, !isEmpty else { return isEmpty ? [] : [self] }
        return stride(from: 0, to: count, by: size).map {
            Array(self[$0..<Swift.min($0 + size, count)])
        }
    }
}
