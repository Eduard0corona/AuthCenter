using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace AuthCenter.Api.Authorization;

public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        // Permission codes are only unique within an application, so AuthCenter's administration
        // permissions count only in a session issued for AuthCenter itself: another application's
        // token must not open this API because one of its permissions shares a code.
        var issuedForAuthCenter = context.User.HasClaim(DomainConstants.Claims.Applications, DomainConstants.SystemCodes.AuthCenter);
        if (issuedForAuthCenter && context.User.HasClaim(DomainConstants.Claims.Permissions, requirement.Permission))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
