using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using ServiceDesk.Api.Auth;
using ServiceDesk.Api.ErrorHandling;

namespace ServiceDesk.Api;

public static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddApiControllers(this IServiceCollection services)
    {
        services
            .AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

        services.AddProblemDetails();
        services.AddExceptionHandler<DomainExceptionHandler>();

        return services;
    }

    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var key = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key is not configured.");

        services.AddSingleton<JwtTokenService>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false; // keep "sub" and "role" exactly as JwtTokenService issues them
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = configuration["Jwt:Issuer"],
                    ValidateAudience = true,
                    ValidAudience = configuration["Jwt:Audience"],
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(key)),
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                };
            });

        // Deny by default for TriageService: a bare [Authorize] and an endpoint with no attribute at all both get
        // the human-roles policy. Only an explicit Policy = ... below lets the service role in.
        services.AddAuthorization(options =>
        {
            var humanUser = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(AppRoles.HumanRoles)
                .Build();

            options.DefaultPolicy = humanUser;
            options.FallbackPolicy = humanUser;

            options.AddPolicy(Policies.CanReadRequests, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole([.. AppRoles.HumanRoles, AppRoles.TriageService]));

            options.AddPolicy(Policies.CanPostTriage, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(AppRoles.TriageService));
        });

        return services;
    }

    // Fails at startup, naming the variable, if the triage client settings are missing or weak.
    public static IServiceCollection AddServiceClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ServiceClientOptions.FromConfiguration(configuration));
        return services;
    }

    public static IServiceCollection AddOpenApiWithBearer(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, ct) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Paste the accessToken returned by POST /api/auth/login.",
                };

                document.Security ??= [];
                document.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
                });

                return Task.CompletedTask;
            });
        });

        return services;
    }
}