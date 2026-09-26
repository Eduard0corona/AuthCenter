using AuthCenter.Client;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

// Authority, ClientId and ClientSecret (from a secret store) are required. Resource names the API
// this BFF calls (see samples/dotnet-api); its scopes must be in Scopes too. With several
// instances, set UseDistributedRefreshCoordination and a shared IDistributedCache.
builder.Services.AddAuthCenterBff(builder.Configuration.GetRequiredSection("AuthCenter"));
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Orders.Read", policy => policy
        .RequireAuthenticatedUser()
        .RequireAuthCenterPermission("ORDERS_READ"));
});

var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthCenterBff();
app.MapGet("/", () => Results.Ok(new
{
    login = "/auth/login?return_url=/",
    session = "/auth/session",
    note = "The browser receives only the encrypted session handle, never OAuth tokens."
}));
app.MapGet("/api/me", [Authorize] (HttpContext context) => Results.Ok(new
{
    subject = context.User.FindFirst("sub")?.Value,
    name = context.User.FindFirst("name")?.Value
}));
app.MapGet("/api/orders", [Authorize(Policy = "Orders.Read")] () =>
    Results.Ok(new { message = "Permission enforced from the AuthCenter access token." }));

app.Run();
