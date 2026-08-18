//
//  LocalVectorStore.swift
//  boringNotch
//
//  本地 JSON 向量库：把文本分块 + embedding 持久化到 Application Support，
//  检索时做暴力余弦相似度（文档量不大时完全够用，无需外部服务/依赖）。
//

import Foundation

final class LocalVectorStore: VectorStoreProviding {
    static let shared = LocalVectorStore()

    var name: String { "本地向量库 (JSON)" }

    var isConfigured: Bool {
        !chunks.isEmpty
    }

    /// 全部已存分块
    private(set) var chunks: [VectorDocument] = []

    private let fileURL: URL

    init(fileURL: URL? = nil) {
        let directory = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)
            .first?
            .appendingPathComponent("DeskPet", isDirectory: true)
            ?? FileManager.default.temporaryDirectory
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        self.fileURL = fileURL ?? directory.appendingPathComponent("vectorstore.json")
        load()
    }

    // MARK: - VectorStoreProviding

    func upsert(_ documents: [VectorDocument]) async throws {
        for document in documents {
            if let index = chunks.firstIndex(where: { $0.id == document.id }) {
                chunks[index] = document
            } else {
                chunks.append(document)
            }
        }
        try persist()
    }

    func search(query: String, embedding: [Double]?, topK: Int) async throws -> [VectorSearchResult] {
        guard let queryVector = embedding, !queryVector.isEmpty else { return [] }
        return chunks
            .compactMap { chunk -> VectorSearchResult? in
                guard let vector = chunk.embedding else { return nil }
                return VectorSearchResult(
                    id: chunk.id,
                    text: chunk.text,
                    metadata: chunk.metadata,
                    score: Self.cosineSimilarity(queryVector, vector)
                )
            }
            .sorted { $0.score > $1.score }
            .prefix(max(1, topK))
            .map { $0 }
    }

    func delete(ids: [String]) async throws {
        let idSet = Set(ids)
        chunks.removeAll { idSet.contains($0.id) }
        try persist()
    }

    func clear() async throws {
        chunks = []
        try persist()
    }

    // MARK: - 便捷操作（文档级管理用）

    /// 删除来自某个源文件的所有分块
    func delete(sourceName: String) async throws {
        chunks.removeAll { $0.metadata["source"] == sourceName }
        try persist()
    }

    /// 按源文件汇总各分块数量
    func chunkCountsBySource() -> [String: Int] {
        var counts: [String: Int] = [:]
        for chunk in chunks {
            let source = chunk.metadata["source"] ?? "unknown"
            counts[source, default: 0] += 1
        }
        return counts
    }

    // MARK: - 持久化

    private func load() {
        guard let data = try? Data(contentsOf: fileURL) else { return }
        chunks = (try? JSONDecoder().decode([VectorDocument].self, from: data)) ?? []
    }

    private func persist() throws {
        let data = try JSONEncoder().encode(chunks)
        try data.write(to: fileURL, options: .atomic)
    }

    // MARK: - 相似度

    static func cosineSimilarity(_ a: [Double], _ b: [Double]) -> Double {
        var dot = 0.0
        var normA = 0.0
        var normB = 0.0
        let count = min(a.count, b.count)
        for i in 0..<count {
            dot += a[i] * b[i]
            normA += a[i] * a[i]
            normB += b[i] * b[i]
        }
        guard normA > 0, normB > 0 else { return 0 }
        return dot / (sqrt(normA) * sqrt(normB))
    }
}
