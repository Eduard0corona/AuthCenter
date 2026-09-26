namespace AuthCenter.Client;

public sealed class AuthCenterBffOptions
{
    public required Uri Authority { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = ["openid", "profile", "email", "offline_access"];
    public string CookieName { get; init; } = "__Host-AuthCenter.Bff";
    public string CallbackPath { get; init; } = "/signin-authcenter";
    public string LoginPath { get; init; } = "/auth/login";
    public string LogoutPath { get; init; } = "/auth/logout";
    public string SessionPath { get; init; } = "/auth/session";
    public string RefreshPath { get; init; } = "/auth/refresh";
    public string RemoteFailurePath { get; init; } = "/auth/error";

    /// <summary>Where AuthCenter returns after RP-initiated logout; register it as a post-logout redirect URI.</summary>
    public string SignedOutCallbackPath { get; init; } = "/signout-callback-authcenter";

    /// <summary>Receives OpenID Connect back-channel logout tokens; register it as the client's back-channel logout URI.</summary>
    public string BackchannelLogoutPath { get; init; } = "/auth/backchannel-logout";
    /// <summary>
    /// RFC 8707 resource indicator of the API the BFF calls with the user's token. Its access
    /// token then has that API as audience; include the API's scopes in <see cref="Scopes"/>.
    /// </summary>
    public string? Resource { get; init; }

    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(8);
    public TimeSpan RefreshBeforeExpiration { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Coordinates refresh-token rotation across instances through the distributed cache (see
    /// <see cref="DistributedAuthCenterRefreshCoordinator"/>). Turn it on when the BFF runs on more
    /// than one instance with a shared <c>IDistributedCache</c> and Data Protection key ring.
    /// </summary>
    public bool UseDistributedRefreshCoordination { get; init; }

    internal void Validate()
    {
        if (Authority is null || !Authority.IsAbsoluteUri || Authority.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(Authority.UserInfo) || !string.IsNullOrEmpty(Authority.Query) ||
            !string.IsNullOrEmpty(Authority.Fragment))
            throw new ArgumentException("AuthCenter authority must be an absolute HTTPS URI.", nameof(Authority));
        if (string.IsNullOrWhiteSpace(ClientId))
            throw new ArgumentException("AuthCenter client ID is required.", nameof(ClientId));
        if (string.IsNullOrWhiteSpace(ClientSecret) || ClientSecret.Length < 32)
            throw new ArgumentException("A confidential OAuth client secret of at least 32 characters is required for the BFF pattern.", nameof(ClientSecret));
        if (Scopes is null || Scopes.Count == 0 || Scopes.Count > 20 ||
            Scopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Length > 128 || scope.Any(char.IsWhiteSpace)))
            throw new ArgumentException("BFF scopes must contain between 1 and 20 valid individual scope values.", nameof(Scopes));
        if (!Scopes.Contains("openid", StringComparer.Ordinal))
            throw new ArgumentException("The BFF scopes must include openid.", nameof(Scopes));
        if (!CookieName.StartsWith("__Host-", StringComparison.Ordinal))
            throw new ArgumentException("The BFF cookie must use the __Host- prefix.", nameof(CookieName));
        if (SessionLifetime <= TimeSpan.Zero || SessionLifetime > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(SessionLifetime));
        if (RefreshBeforeExpiration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RefreshBeforeExpiration));
        if (Resource is not null &&
            (!Uri.TryCreate(Resource, UriKind.Absolute, out var resource) || !string.IsNullOrEmpty(resource.Fragment)))
            throw new ArgumentException("The resource must be an absolute URI without a fragment.", nameof(Resource));

        ValidatePath(CallbackPath, nameof(CallbackPath));
        ValidatePath(LoginPath, nameof(LoginPath));
        ValidatePath(LogoutPath, nameof(LogoutPath));
        ValidatePath(SessionPath, nameof(SessionPath));
        ValidatePath(RefreshPath, nameof(RefreshPath));
        ValidatePath(RemoteFailurePath, nameof(RemoteFailurePath));
        ValidatePath(SignedOutCallbackPath, nameof(SignedOutCallbackPath));
        ValidatePath(BackchannelLogoutPath, nameof(BackchannelLogoutPath));

        string[] paths = [CallbackPath, LoginPath, LogoutPath, SessionPath, RefreshPath, RemoteFailurePath, SignedOutCallbackPath, BackchannelLogoutPath];
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new ArgumentException("Every BFF endpoint and callback path must be unique.");
    }

    private static void ValidatePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('?') || path.Contains('#') || path.Contains('\\') || path.Any(char.IsControl))
            throw new ArgumentException("BFF paths must be local absolute paths.", parameterName);
    }
}
