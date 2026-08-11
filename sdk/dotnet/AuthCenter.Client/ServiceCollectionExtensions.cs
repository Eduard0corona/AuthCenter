using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.Client;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAuthCenterClient(this IServiceCollection services, AuthCenterClientOptions options)
    {
        if (!options.Authority.IsAbsoluteUri || options.Authority.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("AuthCenter authority must be an absolute HTTPS URI.", nameof(options));
        services.AddSingleton(options);
        services.AddHttpClient<AuthCenterClient>(client => client.Timeout = TimeSpan.FromSeconds(15));
        return services;
    }

    public static AuthenticationBuilder AddAuthCenterJwtBearer(this AuthenticationBuilder builder, Uri authority, string audience, string scheme = JwtBearerDefaults.AuthenticationScheme)
    {
        if (!authority.IsAbsoluteUri || authority.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("AuthCenter authority must be an absolute HTTPS URI.", nameof(authority));
        return builder.AddJwtBearer(scheme, options =>
        {
            options.Authority = authority.AbsoluteUri.TrimEnd('/');
            options.Audience = audience;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            };
        });
    }
}
