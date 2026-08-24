using System.IO;
using System.Net.Http;
using DeskPet.Core.Chat;
using DeskPet.Core.RAG;

namespace DeskPet.Windows.Services;

/// <summary>
/// 聊天/RAG 服务组装（T4）：PetChatClient（共享真实 HttpClient，同一实例
/// 实现 IChatClient 与 IEmbeddingService）+ LocalVectorStore + RAGManager
/// （共用 %APPDATA%/DeskPet 数据目录，与 JsonSettingsService 默认路径、
/// RAG 注册表同目录）。
/// </summary>
public static class ChatServices
{
    /// <summary>持久化数据目录（Environment.SpecialFolder.ApplicationData + "DeskPet"）。</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskPet");

    /// <summary>共享 HttpClient：与应用同生命周期（Swift URLSession 语义），不做逐请求重建。</summary>
    private static readonly HttpClient Http = new();

    private static readonly PetChatClient Client = new(AppServices.Settings, Http);

    /// <summary>对话客户端，以 IChatClient 暴露（Ruling 7；AppServices 在其上套 RAG 注入管线）。</summary>
    public static IChatClient Chat => Client;

    /// <summary>同一实例的向量化服务（PetChatClient 同时实现 IEmbeddingService；RAG 管线复用）。</summary>
    public static IEmbeddingService Embedding => Client;

    /// <summary>RAG 向量库（与 <see cref="Rag"/> 共用同一实例，管线检索与导入数据一致）。</summary>
    public static IVectorStore VectorStore { get; } = new LocalVectorStore(DataDirectory);

    /// <summary>RAG 编排（向量库 + 注册表均持久化到 <see cref="DataDirectory"/>，Ruling 6）。</summary>
    public static RAGManager Rag { get; } = new(Client, VectorStore, DataDirectory);
}
