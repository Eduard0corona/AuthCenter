using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

public interface IActionLinkService
{
    string GetActionUrl(ActionLinkPurpose purpose, string? applicationCode = null);
}
