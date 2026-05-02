namespace AuthCenter.Contracts.Requests.Auth;

public class ForgotPasswordRequest
{
    public string Email { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
    /// <summary>URL base del cliente para construir el enlace de reset. Ej: https://app.example.com/reset-password</summary>
    public string? CallbackBaseUrl { get; init; }
}
