using System.Diagnostics;

namespace AuthCenter.Contracts.Responses;

public class ApiResponse<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string>? Details { get; init; }
    public string? TraceId { get; init; }

    public static ApiResponse<T> Ok(T data) =>
        new() { Success = true, Data = data, TraceId = CurrentTraceId() };

    public static ApiResponse<T> Fail(string errorCode, string message, IReadOnlyList<string>? details = null) =>
        new() { Success = false, ErrorCode = errorCode, Message = message, Details = details, TraceId = CurrentTraceId() };

    private static string? CurrentTraceId() => Activity.Current?.TraceId.ToHexString();
}

public class ApiResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string>? Details { get; init; }
    public string? TraceId { get; init; }

    public static ApiResponse Ok(string? message = null) => new() { Success = true, Message = message, TraceId = CurrentTraceId() };

    public static ApiResponse Fail(string errorCode, string message, IReadOnlyList<string>? details = null) =>
        new() { Success = false, ErrorCode = errorCode, Message = message, Details = details, TraceId = CurrentTraceId() };

    private static string? CurrentTraceId() => Activity.Current?.TraceId.ToHexString();
}
