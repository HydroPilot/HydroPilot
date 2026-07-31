using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HydroPilotWeb.Controllers;

/// <summary>
/// Valida el header X-Api-Key contra la clave configurada en TelemetryApiKey.
/// </summary>
public class ApiKeyAuthFilter : IAuthorizationFilter
{
    private readonly IConfiguration _configuration;

    public ApiKeyAuthFilter(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expectedKey = _configuration["TelemetryApiKey"];

        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            context.Result = new StatusCodeResult(500);
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var providedKey) ||
            providedKey != expectedKey)
        {
            context.Result = new UnauthorizedResult();
        }
    }
}
