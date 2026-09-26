using AuthCenter.Client;
using Microsoft.AspNetCore.Authorization;

// Register the API in AuthCenter first (POST /api/api-resources): identifier = AuthCenter:Audience,
// scope orders.read, owned by the application that defines the ORDERS_READ permission.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication().AddAuthCenterJwtBearer(builder.Configuration.GetRequiredSection("AuthCenter"));
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
