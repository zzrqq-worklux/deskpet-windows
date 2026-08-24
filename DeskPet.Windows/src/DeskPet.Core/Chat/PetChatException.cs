using System;

namespace DeskPet.Core.Chat;

/// <summary>
/// 对话/向量化错误类别，对应 Swift <c>PetChatError</c>（PetChatManager.swift 36-60）各 case。
/// Task 5/6 UI 可按 <see cref="PetChatException.Kind"/> 映射为友好提示。
/// </summary>
public enum PetChatErrorKind
{
    /// <summary>对话开关未启用（Swift <c>.notEnabled</c>）。</summary>
    NotEnabled,

    /// <summary>缺少必要配置（Swift <c>.notConfigured(String)</c>，如空 Base URL / Embedding 模型名）。</summary>
    NotConfigured,

    /// <summary>Base URL 无法解析为合法绝对地址（Swift <c>.invalidURL</c>）。</summary>
    InvalidUrl,

    /// <summary>服务返回了无法解析的响应（Swift <c>.invalidResponse</c>）。</summary>
    InvalidResponse,

    /// <summary>HTTP 非 200（Swift <c>.httpError(Int, String)</c>）。</summary>
    HttpError,

    /// <summary>模型没有返回内容（Swift <c>.emptyResponse</c>）。</summary>
    EmptyResponse,
}

/// <summary>
/// 对话/向量化异常，消息文案与 Swift <c>PetChatError.errorDescription</c> 一一对应
/// （PetChatManager.swift 36-60）。<see cref="Kind"/> 供 UI 按类别处理。
/// </summary>
public sealed class PetChatException : Exception
{
    /// <summary>错误类别（对应 Swift <c>PetChatError</c> case）。</summary>
    public PetChatErrorKind Kind { get; }

    /// <summary>HTTP 状态码（仅 <see cref="PetChatErrorKind.HttpError"/> 非空）。</summary>
    public int? StatusCode { get; }

    /// <summary>错误响应体（截断至 300 字符，仅 <see cref="PetChatErrorKind.HttpError"/> 非空）。</summary>
    public string? ErrorBody { get; }

    public PetChatException(
        PetChatErrorKind kind, string message, int? statusCode = null, string? errorBody = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
        ErrorBody = errorBody;
    }

    /// <summary>对话未启用（Swift <c>.notEnabled</c>）。</summary>
    public static PetChatException NotEnabled() =>
        new(PetChatErrorKind.NotEnabled, "请在 设置 → Chat 中启用对话并填写 API 信息");

    /// <summary>缺少必要配置（Swift <c>.notConfigured</c>）。</summary>
    public static PetChatException NotConfigured(string message) =>
        new(PetChatErrorKind.NotConfigured, message);

    /// <summary>Base URL 无效（Swift <c>.invalidURL</c>）。</summary>
    public static PetChatException InvalidUrl() =>
        new(PetChatErrorKind.InvalidUrl, "Base URL 无效");

    /// <summary>服务返回了无法解析的响应（Swift <c>.invalidResponse</c>）。</summary>
    public static PetChatException InvalidResponse() =>
        new(PetChatErrorKind.InvalidResponse, "服务返回了无法解析的响应");

    /// <summary>HTTP 非 200（Swift <c>.httpError</c>，body 截断至 300 字符）。</summary>
    public static PetChatException HttpError(int statusCode, string body) =>
        new(PetChatErrorKind.HttpError, $"请求失败（HTTP {statusCode}）：{body}", statusCode, body);

    /// <summary>模型没有返回内容（Swift <c>.emptyResponse</c>）。</summary>
    public static PetChatException EmptyResponse() =>
        new(PetChatErrorKind.EmptyResponse, "模型没有返回内容");
}
