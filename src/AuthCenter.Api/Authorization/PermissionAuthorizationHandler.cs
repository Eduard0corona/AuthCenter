using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace AuthCenter.Api.Authorization;

public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var hasPermission = context.User.Claims
            .Where(c => c.Type == DomainConstants.Claims.Permissions)
            .Any(c => c.Value == requirement.Permission);

        if (hasPermission)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
