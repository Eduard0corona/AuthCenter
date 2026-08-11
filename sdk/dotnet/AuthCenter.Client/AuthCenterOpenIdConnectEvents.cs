using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

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

    public override Task RemoteFailure(RemoteFailureContext context)
    {
        context.HandleResponse();
        context.Response.Redirect(options.RemoteFailurePath);
        return Task.CompletedTask;
    }
}
