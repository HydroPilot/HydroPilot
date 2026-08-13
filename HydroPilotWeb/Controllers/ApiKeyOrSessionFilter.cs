using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HydroPilotWeb.Controllers;

/// <summary>
/// Autoriza el endpoint si el header X-Api-Key es válido (scripts/IA) o si
/// el usuario tiene una sesión autenticada (cookie). Reemplaza a [Authorize]
/// para permitir ambos mecanismos en el mismo endpoint.
/// </summary>
public class ApiKeyOrSessionFilter : IAuthorizationFilter
{
    private readonly IConfiguration _configuration;

    public ApiKeyOrSessionFilter(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        // 1. Sesión autenticada (cookie de la app)
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
            return;

        // 2. API key válida (scripts)
        if (context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var providedKey))
        {
            var expectedKey = _configuration["TelemetryApiKey"];
            if (!string.IsNullOrWhiteSpace(expectedKey) && providedKey == expectedKey)
                return;
        }

        context.Result = new UnauthorizedResult();
    }
}
