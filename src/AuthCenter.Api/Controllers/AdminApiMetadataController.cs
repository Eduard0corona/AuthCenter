using AuthCenter.Contracts.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController, Route("api/admin-metadata"), Authorize]
public sealed class AdminApiMetadataController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(ApiResponse<object>.Ok(new AdminApiMetadataDto
    {
        MaximumPageSize = 100,
        ErrorCodes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VALIDATION_FAILED"] = "The request failed validation.", ["NOT_FOUND"] = "The resource does not exist.",
            ["FORBIDDEN"] = "The caller lacks permission.", ["REAUTHENTICATION_REQUIRED"] = "A single-use step-up proof is required.",
            ["CONCURRENCY_CONFLICT"] = "The resource changed after it was loaded.", ["IDEMPOTENCY_KEY_REQUIRED"] = "A valid idempotency key is required.",
            ["LAST_SUPER_ADMIN"] = "The operation would remove the last effective SuperAdmin.", ["SYSTEM_ROLE_ASSIGNMENT_FORBIDDEN"] = "The system role cannot be assigned by this caller.",
            ["INTERNAL_ERROR"] = "An unexpected server error occurred; provide traceId to support."
        },
        StepUpPurposes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["admin.user.delete"] = "Delete a user", ["admin.mfa.reset"] = "Reset MFA", ["admin.super-admin.remove"] = "Remove privileged access",
            ["admin.oauth-client.rotate-secret"] = "Rotate an OAuth client secret", ["admin.access-policy.publish"] = "Publish an access policy",
            ["admin.federation.change"] = "Change federation configuration", ["admin.provisioning-token.rotate"] = "Rotate a provisioning token",
            ["admin.provisioning-token.revoke"] = "Revoke a provisioning token", ["admin.event-hook.rotate-secret"] = "Rotate an event hook signing secret"
        },
        OperationPermissions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GET /api/provisioning-tokens"] = "AUTHCENTER_PROVISIONING_READ", ["POST /api/provisioning-tokens"] = "AUTHCENTER_PROVISIONING_WRITE",
            ["POST /api/provisioning-tokens/{id}/rotate"] = "AUTHCENTER_PROVISIONING_WRITE", ["DELETE /api/provisioning-tokens/{id}"] = "AUTHCENTER_PROVISIONING_WRITE",
            ["GET /api/event-hooks"] = "AUTHCENTER_EVENT_HOOKS_READ", ["POST /api/event-hooks"] = "AUTHCENTER_EVENT_HOOKS_WRITE", ["PUT /api/event-hooks/{id}"] = "AUTHCENTER_EVENT_HOOKS_WRITE",
            ["POST /api/event-hooks/{id}/rotate-secret"] = "AUTHCENTER_EVENT_HOOKS_WRITE", ["GET /api/event-hooks/event-types"] = "AUTHCENTER_EVENT_HOOKS_READ",
            ["GET /api/event-hooks/deliveries"] = "AUTHCENTER_EVENT_HOOKS_READ", ["POST /api/event-hooks/deliveries/{id}/replay"] = "AUTHCENTER_EVENT_HOOKS_WRITE",
            ["GET /api/lifecycle/profile-mappings"] = "AUTHCENTER_USERS_READ", ["PUT /api/lifecycle/profile-mappings/{id}"] = "AUTHCENTER_USERS_WRITE",
            ["GET /api/lifecycle/group-rules"] = "AUTHCENTER_GROUPS_READ", ["PUT /api/lifecycle/group-rules/{id}"] = "AUTHCENTER_GROUPS_WRITE",
            ["GET /api/federation/providers"] = "AUTHCENTER_FEDERATION_READ", ["POST /api/federation/providers"] = "AUTHCENTER_FEDERATION_WRITE",
            ["GET /api/federation/routing-rules"] = "AUTHCENTER_FEDERATION_READ", ["PUT /api/federation/routing-rules/{id}"] = "AUTHCENTER_FEDERATION_WRITE",
            ["GET /api/admin-dashboard"] = "AUTHCENTER_AUDIT_LOGS_READ", ["GET /api/audit-logs/export"] = "AUTHCENTER_AUDIT_LOGS_READ"
        }
    }));
}
