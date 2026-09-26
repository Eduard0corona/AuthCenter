using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace AuthCenter.Client;

internal sealed class AuthCenterOpenIdConnectEvents(
    AuthCenterBffOptions options,
    AuthCenterAccessTokenValidator accessTokenValidator) : OpenIdConnectEvents
{
    public override async Task TicketReceived(TicketReceivedContext context)
    {
        var accessToken = context.Properties?.GetTokenValue("access_token");
        if (context.Principal is null || string.IsNullOrWhiteSpace(accessToken))
        {
            context.Fail("AuthCenter did not return a usable access token.");
            return;
        }

        try
        {
            var accessTokenPrincipal = await accessTokenValidator.ValidateAsync(
                accessToken,
                context.HttpContext.RequestAborted);
            AuthCenterAccessTokenPrincipalFactory.Enrich(context.Principal, accessTokenPrincipal);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or Microsoft.IdentityModel.Tokens.SecurityTokenException)
        {
            context.Fail("AuthCenter returned inconsistent token claims.");
        }
    }

    public override Task RedirectToIdentityProvider(RedirectContext context)
    {
        // Per-request parameters set by the login endpoint (prompt and max_age are handled by the
        // OpenID Connect handler itself).
        if (context.Properties.GetParameter<string>(OpenIdConnectParameterNames.LoginHint) is { Length: > 0 } loginHint)
            context.ProtocolMessage.LoginHint = loginHint;
        if (context.Properties.GetParameter<string>(OpenIdConnectParameterNames.AcrValues) is { Length: > 0 } acrValues)
            context.ProtocolMessage.AcrValues = acrValues;
        return Task.CompletedTask;
    }

    public override Task RemoteFailure(RemoteFailureContext context)
    {
        context.HandleResponse();
        var error = AuthCenterChallengeParameters.ForwardedError(context.Failure);
        context.Response.Redirect(error is null
            ? options.RemoteFailurePath
            : $"{options.RemoteFailurePath}?error={Uri.EscapeDataString(error)}");
        return Task.CompletedTask;
    }
}
