using HydroPilotWeb.Components;
using HydroPilotWeb.Data;
using HydroPilotWeb.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

var useLocalDb = builder.Configuration.GetValue("HydroPilot:UseLocalDb", false);
var connectionStringName = useLocalDb ? "LocalSqlServer" : "AzureSql";
var connectionString = builder.Configuration.GetConnectionString(connectionStringName)
    ?? throw new InvalidOperationException($"Falta la cadena de conexión '{connectionStringName}'.");

builder.Services.AddDbContextFactory<HydroPilotDbContext>(options =>
    options.UseSqlServer(connectionString, sqlOptions =>
        sqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null)));

builder.Services.AddScoped<UserService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    })
    .AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"]!;
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]!;
        options.SaveTokens = true;
        options.Events.OnCreatingTicket = async ctx =>
        {
            var userService = ctx.HttpContext.RequestServices.GetRequiredService<UserService>();
            var user = await userService.FindOrCreateAsync(ctx.Principal!);

            var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
            identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
            identity.AddClaim(new Claim("user_id", user.Id.ToString()));

            if (user.Role == "no-asignado")
            {
                ctx.Properties.RedirectUri = "/no-asignado";
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAdministrador", policy =>
        policy.RequireClaim(ClaimTypes.Role, "Administrador"));

    options.AddPolicy("RequireAssigned", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.HasClaim(ClaimTypes.Role, "Administrador") ||
            ctx.User.HasClaim(ClaimTypes.Role, "Operador") ||
            ctx.User.HasClaim(ClaimTypes.Role, "Productor") ||
            ctx.User.HasClaim(ClaimTypes.Role, "Soporte Técnico")));
});
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HydroPilotDbContext>>();
    using var context = factory.CreateDbContext();
    DbInitializer.Initialize(context, builder.Configuration);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

if(!useLocalDb)
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

app.MapStaticAssets();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/login-google", async (HttpContext context) =>
{
    var returnUrl = context.Request.Query["returnUrl"].FirstOrDefault() ?? "/";
    await context.ChallengeAsync("Google", new AuthenticationProperties
    {
        RedirectUri = returnUrl,
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
    });
});

app.MapPost("/login-admin", async (HttpContext context, UserService userService) =>
{
    var form = await context.Request.ReadFormAsync();
    var password = form["password"].FirstOrDefault();

    if (string.IsNullOrWhiteSpace(password))
    {
        return Results.Redirect("/login?error=password_required");
    }

    var admin = await userService.FindAdminByPasswordAsync(password);

    if (admin == null)
    {
        return Results.Redirect("/login?error=invalid_password");
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, "Administrador"),
        new(ClaimTypes.Email, admin.Email),
        new(ClaimTypes.NameIdentifier, admin.GoogleSub),
        new(ClaimTypes.Role, admin.Role),
        new("user_id", admin.Id.ToString())
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
    });

    return Results.Redirect("/");
});

app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.Run();
