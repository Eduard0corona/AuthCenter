using AuthCenter.Client;

var builder = WebApplication.CreateBuilder(args);
var section = builder.Configuration.GetSection("AuthCenter");
builder.Services.AddAuthCenterClient(new AuthCenterClientOptions
{
    Authority = new Uri(section["Authority"] ?? throw new InvalidOperationException("Set AuthCenter:Authority.")),
    ClientId = section["ClientId"] ?? throw new InvalidOperationException("Set AuthCenter:ClientId."),
    ClientSecret = section["ClientSecret"]
});
var app = builder.Build();
app.UseHttpsRedirection();
app.MapGet("/login", (AuthCenterClient client, HttpContext context) =>
{
    var pkce = AuthCenterClient.CreatePkce();
    context.Response.Cookies.Append("sample.pkce", pkce.Verifier, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10) });
    var redirect = new Uri($"{context.Request.Scheme}://{context.Request.Host}/callback");
    return Results.Redirect(client.BuildAuthorizationUri(redirect, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), pkce).AbsoluteUri);
});
app.MapGet("/callback", async (string code, AuthCenterClient client, HttpContext context, CancellationToken ct) =>
{
    if (!context.Request.Cookies.TryGetValue("sample.pkce", out var verifier)) return Results.BadRequest("PKCE cookie missing.");
    var redirect = new Uri($"{context.Request.Scheme}://{context.Request.Host}/callback");
    var tokens = await client.ExchangeCodeAsync(code, redirect, verifier, ct);
    return Results.Ok(new { tokens.TokenType, tokens.ExpiresIn }); // Never render token material.
});
app.Run();
