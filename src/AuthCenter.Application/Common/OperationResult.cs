namespace AuthCenter.Application.Common;

public class OperationResult<T>
{
    public bool IsSuccess { get; private init; }
    public T? Data { get; private init; }
    public string ErrorCode { get; private init; } = string.Empty;
    public string Message { get; private init; } = string.Empty;
    public IReadOnlyList<string> Details { get; private init; } = [];

    public static OperationResult<T> Success(T data) =>
        new() { IsSuccess = true, Data = data };

    public static OperationResult<T> Failure(string errorCode, string message, IReadOnlyList<string>? details = null) =>
        new() { IsSuccess = false, ErrorCode = errorCode, Message = message, Details = details ?? [] };
}

public class OperationResult
{
    public bool IsSuccess { get; private init; }
    public string ErrorCode { get; private init; } = string.Empty;
    public string Message { get; private init; } = string.Empty;
    public IReadOnlyList<string> Details { get; private init; } = [];

    public static OperationResult Success() => new() { IsSuccess = true };

    public static OperationResult Failure(string errorCode, string message, IReadOnlyList<string>? details = null) =>
        new() { IsSuccess = false, ErrorCode = errorCode, Message = message, Details = details ?? [] };
}
