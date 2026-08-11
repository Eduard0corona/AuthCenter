using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace AuthCenter.Client;

/// <summary>
/// Adds the current BFF session access token to server-side downstream API calls.
/// The token never crosses the browser boundary.
/// </summary>
public sealed class AuthCenterBffAccessTokenHandler(
    IHttpContextAccessor contextAccessor,
    IAuthCenterBffSessionManager sessions) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var context = contextAccessor.HttpContext
            ?? throw new InvalidOperationException("An active HTTP request is required to use the AuthCenter BFF session.");
        var accessToken = await sessions.GetAccessTokenAsync(context, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("The AuthCenter BFF session is not authenticated or cannot be refreshed.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
