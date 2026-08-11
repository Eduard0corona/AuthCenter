using AuthCenter.Client;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);
var authority = new Uri(builder.Configuration["AuthCenter:Authority"] ?? throw new InvalidOperationException("Set AuthCenter:Authority."));
var audience = builder.Configuration["AuthCenter:Audience"] ?? throw new InvalidOperationException("Set AuthCenter:Audience.");
builder.Services.AddAuthentication().AddAuthCenterJwtBearer(authority, audience);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Orders.Read", policy => policy
        .RequireAuthenticatedUser()
        .RequireAuthCenterScope("orders.read")
        .RequireAuthCenterPermission("ORDERS_READ"));
});
var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/api/private", [Authorize] () => Results.Ok(new { message = "Token validated by AuthCenter discovery/JWKS." }));
app.MapGet("/api/orders", [Authorize(Policy = "Orders.Read")] () => Results.Ok(new { message = "Scope and permission validated." }));
app.Run();
