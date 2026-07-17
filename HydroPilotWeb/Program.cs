using HydroPilotWeb.Components;
using HydroPilotWeb.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
