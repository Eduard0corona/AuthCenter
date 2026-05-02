using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Audit;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/audit-logs")]
[Authorize]
public class AuditLogsController : ControllerBase
{
    private readonly IAuditService _auditService;

    public AuditLogsController(IAuditService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.AuditLogsRead)]
    public async Task<IActionResult> Get([FromQuery] AuditLogQuery query, CancellationToken ct)
    {
        var result = await _auditService.GetAsync(query, ct);
        return Ok(ApiResponse<object>.Ok(result));
    }
}
