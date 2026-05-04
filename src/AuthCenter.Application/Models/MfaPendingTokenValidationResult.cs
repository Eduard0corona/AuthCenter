namespace AuthCenter.Application.Models;

public record MfaPendingTokenValidationResult(Guid UserId, string ApplicationCode, string TokenId);
