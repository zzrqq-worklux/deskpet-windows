//
//  RAGManager.swift
//  boringNotch
//
//  RAG 数据流编排：导入文档 → 分块 → 生成向量 → 写入本地向量库。
//  对话侧的检索与上下文注入已由 PetChatManager 完成，这里只管"喂数据"。
//

import Defaults
import Foundation

/// 文档参与对话的方式。
enum RAGDocumentRole: String, CaseIterable, Codable, Identifiable, Defaults.Serializable {
    case fixed = "Fixed"
    case retrieval = "Retrieval"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .fixed: return "固定注入"
        case .retrieval: return "按需检索"
        }
    }
}

/// 已导入的一份文档（设置页展示 / 管理用）
struct RAGDocument: Identifiable, Codable, Equatable, Defaults.Serializable {
    let id: UUID
    var sourcePath: String
    var sourceName: String
    var chunkCount: Int
    var importedAt: Date
    var role: RAGDocumentRole

    init(id: UUID, sourcePath: String, sourceName: String, chunkCount: Int, importedAt: Date, role: RAGDocumentRole = .retrieval) {
        self.id = id
        self.sourcePath = sourcePath
        self.sourceName = sourceName
        self.chunkCount = chunkCount
        self.importedAt = importedAt
        self.role = role
    }

    enum CodingKeys: String, CodingKey {
        case id, sourcePath, sourceName, chunkCount, importedAt, role
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        sourcePath = try container.decode(String.self, forKey: .sourcePath)
        sourceName = try container.decode(String.self, forKey: .sourceName)
        chunkCount = try container.decode(Int.self, forKey: .chunkCount)
        importedAt = try container.decode(Date.self, forKey: .importedAt)
        role = try container.decodeIfPresent(RAGDocumentRole.self, forKey: .role) ?? .retrieval
    }
}

/// 文本分块
struct TextChunk: Identifiable {
    let id: UUID
    let index: Int
    let text: String
}

enum RAGError: LocalizedError {
    case emptyFile(String)
    case unreadableFile(String)

    var errorDescription: String? {
        switch self {
        case .emptyFile(let name):
            return "「\(name)」没有可导入的文本内容"
        case .unreadableFile(let name):
            return "「\(name)」不是可读的文本文件（支持 UTF-8/UTF-16）"
        }
    }
}

@MainActor
final class RAGManager: ObservableObject {
    static let shared = RAGManager()

    @Published private(set) var documents: [RAGDocument] = []
    @Published private(set) var isImporting = false
    /// 0-1 导入进度
    @Published private(set) var importProgress: Double = 0
    @Published private(set) var importStatus: String?
    @Published private(set) var lastError: String?

    private init() {
        documents = Defaults[.petRAGDocuments]
    }

    // MARK: - 导入

    /// 导入一组文件：读取 → 分块 → 批量嵌入 → 写入向量库（同名文件覆盖重导）
    func importFiles(urls: [URL], role: RAGDocumentRole = .retrieval) async {
        guard !isImporting else { return }
        isImporting = true
        lastError = nil
        importProgress = 0
        importStatus = nil

        defer { isImporting = false }

        do {
            // 1) 读取并分块（本地操作，快）
            var pending: [(url: URL, chunks: [TextChunk], role: RAGDocumentRole)] = []
            var firstError: String?
            for url in urls {
                do {
                    let text = try readText(from: url)
                    let chunks = chunkText(
                        text,
                        size: Defaults[.petRAGChunkSize],
                        overlap: Defaults[.petRAGChunkOverlap]
                    )
                    guard !chunks.isEmpty else {
                        firstError = RAGError.emptyFile(url.lastPathComponent).localizedDescription
                        continue
                    }
                    pending.append((url, chunks, role))
                } catch {
                    firstError = error.localizedDescription
                }
            }
            guard !pending.isEmpty else {
                lastError = firstError ?? "没有可导入的文件"
                return
            }

            let total = pending.reduce(0) { $0 + $1.chunks.count }
            var processed = 0

            // 2) 逐文件：先清旧 → 分批嵌入 → 写入
            for item in pending {
                // 重导同名文件：先删除旧分块
                try? await VectorStoreManager.shared.deleteChunks(sourceName: item.url.lastPathComponent)

                var documents: [VectorDocument] = []
                for batch in item.chunks.batched(by: 16) {
                    let embeddings = try await PetChatManager.shared.embed(batch.map(\.text))
                    for (i, chunk) in batch.enumerated() {
                        documents.append(
                            VectorDocument(
                                id: chunk.id.uuidString,
                                text: chunk.text,
                                metadata: [
                                    "source": item.url.lastPathComponent,
                                    "sourcePath": item.url.path,
                                    "chunkIndex": String(chunk.index),
                                    "role": item.role.rawValue,
                                ],
                                embedding: embeddings[i]
                            )
                        )
                    }
                    processed += batch.count
                    importProgress = Double(processed) / Double(total)
                    importStatus = "\(item.url.lastPathComponent) · \(processed)/\(total)"
                }

                try await VectorStoreManager.shared.upsertChunks(documents)
                updateRegistry(url: item.url, chunkCount: documents.count, role: item.role)
            }

            importStatus = "导入完成，共 \(total) 个分块"
        } catch {
            lastError = error.localizedDescription
            importStatus = nil
        }
    }

    // MARK: - 删除 / 清空

    func removeDocument(_ document: RAGDocument) async {
        guard let index = documents.firstIndex(where: { $0.id == document.id }) else { return }
        try? await VectorStoreManager.shared.deleteChunks(sourceName: document.sourceName)
        documents.remove(at: index)
        persistRegistry()
    }

    func clearAll() async {
        try? await VectorStoreManager.shared.clearAllChunks()
        documents = []
        persistRegistry()
    }

    // MARK: - 私有

    private func readText(from url: URL) throws -> String {
        let data = try Data(contentsOf: url)
        if let text = String(data: data, encoding: .utf8) {
            return text
        }
        if let text = String(data: data, encoding: .utf16) {
            return text
        }
        throw RAGError.unreadableFile(url.lastPathComponent)
    }

    private func updateRegistry(url: URL, chunkCount: Int, role: RAGDocumentRole) {
        if let index = documents.firstIndex(where: { $0.sourcePath == url.path }) {
            documents[index].chunkCount = chunkCount
            documents[index].importedAt = Date()
            documents[index].role = role
        } else {
            documents.append(
                RAGDocument(
                    id: UUID(),
                    sourcePath: url.path,
                    sourceName: url.lastPathComponent,
                    chunkCount: chunkCount,
                    importedAt: Date(),
                    role: role
                )
            )
        }
        persistRegistry()
    }

    private func persistRegistry() {
        Defaults[.petRAGDocuments] = documents
    }

    // MARK: - 分块

    /// 按字符数分块，优先在换行处断开；相邻块按 overlap 重叠，保证语义不割裂。
    func chunkText(_ text: String, size: Int, overlap: Int) -> [TextChunk] {
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n")
        let ns = normalized as NSString
        let length = ns.length
        guard length > 0 else { return [] }

        let maxSize = max(100, size)
        let overlapAmount = min(overlap, maxSize - 1)
        var result: [TextChunk] = []
        var start = 0
        var index = 0

        while start < length {
            var end = min(start + maxSize, length)
            // 尽量在段落末尾断开，避免把句子拦腰截断
            if end < length {
                let searchStart = start + max(0, maxSize / 2)
                let range = NSRange(location: searchStart, length: end - searchStart)
                let newline = ns.range(of: "\n", options: .backwards, range: range)
                if newline.location != NSNotFound {
                    end = newline.location + 1
                }
            }
            let piece = ns.substring(with: NSRange(location: start, length: end - start))
            let trimmed = piece.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty {
                result.append(TextChunk(id: UUID(), index: index, text: trimmed))
                index += 1
            }
            if end >= length { break }
            start = max(start + 1, end - overlapAmount)
        }
        return result
    }
}
