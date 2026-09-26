using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Audit;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;

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

    [HttpGet("export")]
    [Authorize(Policy = DomainConstants.Permissions.AuditLogsRead)]
    public async Task<IActionResult> Export([FromQuery] AuditLogQuery query, CancellationToken ct)
    {
        var items = await _auditService.ExportPageAsync(query, ct);
        var csv = new StringBuilder("id,createdAt,action,actorId,actorEmail,applicationCode,entityName,entityId,ipAddress,traceId\r\n");
        foreach (var item in items)
            csv.AppendJoin(',', Csv(item.Id), Csv(item.CreatedAt.ToString("O")), Csv(item.Action), Csv(item.UserId), Csv(item.UserEmail), Csv(item.ApplicationCode), Csv(item.EntityName), Csv(item.EntityId), Csv(item.IpAddress), Csv(item.TraceId)).Append("\r\n");
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"authcenter-system-log-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
    }

    // Quoted, and a leading formula character is neutralized so a spreadsheet never evaluates it.
    private static string Csv(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            text = "'" + text;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
