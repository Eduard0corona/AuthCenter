using AuthCenter.Client;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);
var authority = new Uri(builder.Configuration["AuthCenter:Authority"] ?? throw new InvalidOperationException("Set AuthCenter:Authority."));
var audience = builder.Configuration["AuthCenter:Audience"] ?? throw new InvalidOperationException("Set AuthCenter:Audience.");
builder.Services.AddAuthentication().AddAuthCenterJwtBearer(authority, audience);
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/api/private", [Authorize] () => Results.Ok(new { message = "Token validated by AuthCenter discovery/JWKS." }));
app.Run();
