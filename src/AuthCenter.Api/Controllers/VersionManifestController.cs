using System.Reflection;
using AuthCenter.Contracts.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController, Route("api/version"), AllowAnonymous]
public sealed class VersionManifestController : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Get()
    {
        var assembly = typeof(Program).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var candidate = informational.Split('+').LastOrDefault();
        var commit = candidate is { Length: >= 7 and <= 64 } && candidate.All(Uri.IsHexDigit) ? candidate : null;
        return Ok(ApiResponse<object>.Ok(new VersionManifestDto { Version = informational.Split('+')[0], Commit = commit }));
    }
}
