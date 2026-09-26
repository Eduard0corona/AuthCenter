using AuthCenter.Contracts.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController, Route("api/admin-metadata"), Authorize]
public sealed class AdminApiMetadataController(IHostEnvironment environment, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(ApiResponse<object>.Ok(new AdminApiMetadataDto
    {
        MaximumPageSize = 100,
        EnvironmentName = configuration["AdminConsole:EnvironmentName"] is { Length: > 0 } configured ? configured : environment.EnvironmentName,
        ErrorCodes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VALIDATION_FAILED"] = "The request failed validation.", ["NOT_FOUND"] = "The resource does not exist.",
            ["FORBIDDEN"] = "The caller lacks permission.", ["REAUTHENTICATION_REQUIRED"] = "A single-use step-up proof is required.",
            ["CONCURRENCY_CONFLICT"] = "The resource changed after it was loaded.", ["IDEMPOTENCY_KEY_REQUIRED"] = "A valid idempotency key is required.",
            ["LAST_SUPER_ADMIN"] = "The operation would remove the last effective SuperAdmin.", ["SYSTEM_ROLE_ASSIGNMENT_FORBIDDEN"] = "The system role cannot be assigned by this caller.",
            ["SOD_CONFLICT"] = "The change would give a user two roles a separation of duties rule keeps apart.",
            ["SELF_APPROVAL_FORBIDDEN"] = "Nobody decides their own access request.", ["SELF_REVIEW_FORBIDDEN"] = "Nobody reviews their own access.",
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
            ["GET /api/admin-dashboard"] = "AUTHCENTER_AUDIT_LOGS_READ", ["GET /api/audit-logs/export"] = "AUTHCENTER_AUDIT_LOGS_READ",
            ["GET /api/provisioning-tokens/{id}/diagnostics"] = "AUTHCENTER_PROVISIONING_READ", ["GET /api/provisioning-tokens/{id}/requests"] = "AUTHCENTER_PROVISIONING_READ",
            ["GET /api/saml/identity-provider"] = "AUTHCENTER_SAML_APPS_READ", ["GET /api/saml/service-providers"] = "AUTHCENTER_SAML_APPS_READ",
            ["POST /api/saml/service-providers"] = "AUTHCENTER_SAML_APPS_WRITE", ["PUT /api/saml/service-providers/{id}"] = "AUTHCENTER_SAML_APPS_WRITE",
            ["DELETE /api/saml/service-providers/{id}"] = "AUTHCENTER_SAML_APPS_WRITE", ["POST /api/saml/service-providers/parse-metadata"] = "AUTHCENTER_SAML_APPS_WRITE",
            ["GET /api/governance/applications/{id}"] = "AUTHCENTER_GOVERNANCE_READ", ["PUT /api/governance/applications/{id}"] = "AUTHCENTER_GOVERNANCE_WRITE",
            ["GET /api/governance/access-requests"] = "AUTHCENTER_GOVERNANCE_READ", ["POST /api/governance/access-requests/{id}/approve"] = "AUTHCENTER_GOVERNANCE_WRITE",
            ["POST /api/governance/access-requests/{id}/reject"] = "AUTHCENTER_GOVERNANCE_WRITE", ["GET /api/governance/access-reviews"] = "AUTHCENTER_GOVERNANCE_READ",
            ["POST /api/governance/access-reviews"] = "AUTHCENTER_GOVERNANCE_WRITE", ["POST /api/governance/access-reviews/{id}/items/{itemId}/decision"] = "AUTHCENTER_GOVERNANCE_WRITE",
            ["POST /api/governance/access-reviews/{id}/cancel"] = "AUTHCENTER_GOVERNANCE_WRITE", ["GET /api/governance/sod-rules"] = "AUTHCENTER_GOVERNANCE_READ",
            ["POST /api/governance/sod-rules"] = "AUTHCENTER_GOVERNANCE_WRITE", ["PUT /api/governance/sod-rules/{id}"] = "AUTHCENTER_GOVERNANCE_WRITE",
            ["DELETE /api/governance/sod-rules/{id}"] = "AUTHCENTER_GOVERNANCE_WRITE", ["GET /api/governance/sod-violations"] = "AUTHCENTER_GOVERNANCE_READ"
        }
    }));
}
