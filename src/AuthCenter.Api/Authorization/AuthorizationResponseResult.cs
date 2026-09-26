using System.Net;
using System.Text;
using AuthCenter.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Authorization;

/// <summary>
/// Delivers an authorization response to the client's registered redirect URI: a redirect for
/// response_mode=query or an auto-submitted HTML form for response_mode=form_post.
/// </summary>
public static class AuthorizationResponseResult
{
    public static IActionResult Create(AuthorizationResponse response)
    {
        if (!response.IsFormPost)
            return new RedirectResult(response.ToRedirectUrl());

        var html = new StringBuilder()
            .Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>Continuando…</title>")
            .Append("<script src=\"/assets/form-post.js\" defer></script></head><body>")
            .Append("<form method=\"post\" action=\"").Append(WebUtility.HtmlEncode(response.RedirectUri)).Append("\">");
        foreach (var (name, value) in response.Parameters)
        {
            html.Append("<input type=\"hidden\" name=\"").Append(WebUtility.HtmlEncode(name))
                .Append("\" value=\"").Append(WebUtility.HtmlEncode(value)).Append("\">");
        }
        html.Append("<noscript><button type=\"submit\">Continuar</button></noscript></form></body></html>");

        return new FormPostResult(html.ToString(), new Uri(response.RedirectUri).GetLeftPart(UriPartial.Authority));
    }

    private sealed class FormPostResult(string html, string formAction) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var httpResponse = context.HttpContext.Response;
            httpResponse.StatusCode = StatusCodes.Status200OK;
            httpResponse.ContentType = "text/html; charset=utf-8";
            httpResponse.Headers.CacheControl = "no-store";
            httpResponse.Headers.Pragma = "no-cache";
            httpResponse.Headers["Content-Security-Policy"] =
                $"default-src 'none'; script-src 'self'; form-action {formAction}; base-uri 'none'; frame-ancestors 'none'";
            await httpResponse.WriteAsync(html, Encoding.UTF8);
        }
    }
}
