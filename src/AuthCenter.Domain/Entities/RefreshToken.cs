namespace AuthCenter.Domain.Entities;

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ApplicationCode { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public string? OAuthClientId { get; set; }
    public string? GrantedScopes { get; set; }

    /// <summary>For OAuth grants: space-separated API resource identifiers the grant covers.</summary>
    public string? GrantedResources { get; set; }
    public Guid? TokenFamilyId { get; set; }
    public DateTime? AbsoluteExpiresAt { get; set; }

    /// <summary>When the user actually authenticated for this session (OIDC auth_time).</summary>
    public DateTime? AuthenticatedAt { get; set; }

    /// <summary>Space-separated RFC 8176 authentication method references (OIDC amr).</summary>
    public string? AuthenticationMethods { get; set; }

    /// <summary>The <see cref="Enums.AuthenticationAssuranceLevel"/> reached by the authentication.</summary>
    public int? AssuranceLevel { get; set; }

    /// <summary>
    /// For OAuth grants: the first-party single sign-on session that authorized them, so ending that
    /// session can end every application grant it produced.
    /// </summary>
    public Guid? SessionId { get; set; }

    public bool IsActive => RevokedAt is null && DateTime.UtcNow < ExpiresAt;

    public ApplicationUser User { get; set; } = null!;
}
