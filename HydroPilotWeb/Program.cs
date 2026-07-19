using HydroPilotWeb.Components;
using HydroPilotWeb.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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
    });

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var useLocalDatabase = builder.Configuration.GetValue("HydroPilot:UseLocalDb", false);
var azureSqlConnectionString = builder.Configuration.GetConnectionString("AzureSql");

builder.Services.AddDbContextFactory<HydroPilotDbContext>(options =>
{
    if (useLocalDatabase)
    {
        var sqlitePath = Path.Combine(builder.Environment.ContentRootPath, "hydropilot.db");
        options.UseSqlite($"Data Source={sqlitePath}");
        return;
    }

    if (string.IsNullOrWhiteSpace(azureSqlConnectionString))
    {
        throw new InvalidOperationException("Falta la cadena de conexión AzureSql o activa HydroPilot:UseLocalDb=true para desarrollo local.");
    }

    options.UseSqlServer(azureSqlConnectionString);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HydroPilotDbContext>>();
    using var context = factory.CreateDbContext();
    DbInitializer.Initialize(context);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

if(!useLocalDatabase)
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

app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.Run();
