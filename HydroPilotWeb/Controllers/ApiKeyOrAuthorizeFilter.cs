using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HydroPilotWeb.Controllers;

/// <summary>
/// Acepta el endpoint si el header X-Api-Key es válido (scripts/IA).
/// Si no hay header, deja pasar al [Authorize] normal (usuarios logueados).
/// Si hay header pero es inválido → 401.
/// </summary>
public class ApiKeyOrAuthorizeFilter : IAuthorizationFilter
{
    private readonly IConfiguration _configuration;

    public ApiKeyOrAuthorizeFilter(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var providedKey))
            return; // sin key → lo resuelve [Authorize]

        var expectedKey = _configuration["TelemetryApiKey"];
        if (string.IsNullOrWhiteSpace(expectedKey) || providedKey != expectedKey)
        {
            context.Result = new UnauthorizedResult();
        }
    }
}
