using HydroPilotWeb.Components;
using HydroPilotWeb.Data;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Anomalies;
using HydroPilotWeb.Services.Dashboard;
using HydroPilotWeb.Services.Forecasting;
using HydroPilotWeb.Services.Lotes;
using HydroPilotWeb.Services.Optimization;
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
        sqlOptions.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null)));

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<HydroPilotWeb.Services.Admin.AdminMaintenanceService>();
builder.Services.AddScoped<GddService>();
builder.Services.AddScoped<YieldService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<SystemParametersService>();
builder.Services.AddScoped<ForecastService>();
builder.Services.AddScoped<TelemetryValidationService>();
builder.Services.AddScoped<TelemetryIngestionService>();
builder.Services.AddScoped<NodeLotAssignmentService>();
builder.Services.AddOptions<TelemetryOptions>().BindConfiguration(TelemetryOptions.SectionName);
builder.Services.AddHostedService<NodeConnectionMonitorHostedService>();
builder.Services.AddHttpClient<WeatherService>(client =>
    client.Timeout = TimeSpan.FromSeconds(25)); // F-03: timeout explícito para OpenWeather
builder.Services.AddHostedService<WeatherFetcherHostedService>();

// --- Dominio de lotes y plantas (plan 09) ---
// El proveedor de riesgo lo reemplazará el módulo de anomalies registrando su
// implementación DESPUÉS de esta línea (último registro gana en DI).
builder.Services.AddSingleton<IPlantRiskProvider, NoPlantRiskProvider>();
builder.Services.AddScoped<LotAggregateService>();
builder.Services.AddScoped<PlantEvaluationService>();
builder.Services.AddScoped<PlantLifecycleService>();
builder.Services.AddScoped<LotDailyFlowService>();
builder.Services.AddScoped<LotOverviewService>();
builder.Services.AddScoped<LotCreationService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<BabyLeafOutlookService>();

// --- Módulo de anomalías (plan 15 / ANO-01..09) ---
// El proveedor de riesgo REAL se registra después del NoPlantRiskProvider: gana el último.
builder.Services.AddSingleton<IPlantRiskProvider, AnomalyRiskProvider>();
builder.Services.AddOptions<AnomalyOptions>().BindConfiguration(AnomalyOptions.SectionName);
builder.Services.AddSingleton<AnomalyLotRiskEvaluator>();
builder.Services.AddSingleton<AnomalyEventService>();
builder.Services.AddScoped<AnomalyQueryService>();
builder.Services.AddScoped<SolutionStatusService>();
builder.Services.AddHostedService<AnomalySweepHostedService>();

// --- Dashboard (plan 12) ---
builder.Services.AddScoped<DashboardService>();
builder.Services.AddOptions<DashboardOptions>().BindConfiguration(DashboardOptions.SectionName);
// --- Módulo de reportes (plan 13): consultas y exportación bajo demanda ---
builder.Services.AddScoped<HydroPilotWeb.Services.Reports.ReportQueryService>();

// --- Simulación (plan 14): what-if sin escritura productiva; API preview sin persistencia (SIM-06) ---
// La frontera de hardware (IHardwareGateway) NO se registra: la simulación nunca
// invoca hardware (SIM-07), y el test de aislamiento lo verifica con un adaptador falso.
builder.Services.AddScoped<HydroPilotWeb.Services.Simulation.ClimateScenarioProvider>();
builder.Services.AddScoped<HydroPilotWeb.Services.Simulation.SimulationService>();

// --- Módulo de optimización: recomendaciones R1–R6 con la lógica de Lotes, Simulación y Anomalías ---
// Se recalculan al abrir la vista y cada SweepIntervalMinutes (barrido); las nuevas de prioridad alta
// se avisan a Notificaciones una sola vez.
builder.Services.AddScoped<IOptimizationEventSink, HydroPilotWeb.Services.Notifications.OptimizationNotificationSink>();
builder.Services.AddScoped<OptimizationService>();
builder.Services.AddOptions<OptimizationOptions>()
    .BindConfiguration(OptimizationOptions.SectionName);
builder.Services.AddHostedService<OptimizationSweepHostedService>();

// --- Control de pH y CE (dosis manual + motor automático) ---
// La Pi pide sus comandos por /api/nodos/{nodeId}/comandos y responde el resultado. Al llegar lecturas de pH/CE
// la ingesta avisa al ControlWorker, que evalúa el tanque si el nodo está en AUTO; el barrido vence lo que nadie
// tomó, mide el efecto de las dosis y guarda el nivel vigente de cada lote (notas/control/plan-control-ph-ce.md).
builder.Services.AddOptions<HydroPilotWeb.Services.Control.ControlOptions>()
    .BindConfiguration(HydroPilotWeb.Services.Control.ControlOptions.SectionName);
builder.Services.AddSingleton<HydroPilotWeb.Services.Control.ControlTrigger>();

// Transporte con la Pi: HTTP (la Pi consulta sus comandos) o Azure IoT Hub (C2D/D2C), con IotHub:Enabled.
// Las cadenas del hub van en user-secrets o en la configuración de la Web App (notas/control/azure-iot-hub.md).
builder.Services.AddOptions<HydroPilotWeb.Services.Control.IotHub.IotHubOptions>()
    .BindConfiguration(HydroPilotWeb.Services.Control.IotHub.IotHubOptions.SectionName);
builder.Services.AddScoped<HydroPilotWeb.Services.Control.IotHub.IotHubMessageHandler>();
var iotHub = builder.Configuration.GetSection(HydroPilotWeb.Services.Control.IotHub.IotHubOptions.SectionName)
    .Get<HydroPilotWeb.Services.Control.IotHub.IotHubOptions>() ?? new();
if (iotHub.Enabled)
{
    if (string.IsNullOrWhiteSpace(iotHub.ServiceConnectionString) || string.IsNullOrWhiteSpace(iotHub.EventHubConnectionString))
        throw new InvalidOperationException("IotHub:Enabled=true pero faltan IotHub:ServiceConnectionString o IotHub:EventHubConnectionString.");
    builder.Services.AddSingleton<HydroPilotWeb.Services.Control.ICommandChannel, HydroPilotWeb.Services.Control.IotHub.IotHubCommandChannel>();
    builder.Services.AddHostedService<HydroPilotWeb.Services.Control.IotHub.IotHubConsumerHostedService>();
    builder.Services.AddHostedService<HydroPilotWeb.Services.Control.IotHub.IotHubFeedbackHostedService>();
}
else
{
    builder.Services.AddSingleton<HydroPilotWeb.Services.Control.ICommandChannel, HydroPilotWeb.Services.Control.HttpPollingCommandChannel>();
}
builder.Services.AddScoped<HydroPilotWeb.Services.Control.IControlEventSink, HydroPilotWeb.Services.Notifications.ControlNotificationSink>();
builder.Services.AddScoped<HydroPilotWeb.Services.Control.ControlService>();
// Receta aprendida (fase 5): el histórico de ciclos cerrados es el de Lotes (BabyLeafOutlookService).
builder.Services.AddScoped<ILotHistorySource>(sp => sp.GetRequiredService<BabyLeafOutlookService>());
builder.Services.AddScoped<HydroPilotWeb.Services.Control.RecipeService>();
builder.Services.AddHostedService<HydroPilotWeb.Services.Control.ControlWorker>();
builder.Services.AddHostedService<HydroPilotWeb.Services.Control.ControlSweepHostedService>();

// --- Módulo de notificaciones y correo (plan 17) ---
builder.Services.Configure<HydroPilotWeb.Services.Notifications.EmailOptions>(
    builder.Configuration.GetSection(HydroPilotWeb.Services.Notifications.EmailOptions.SectionName));
var emailProvider = builder.Configuration.GetValue<string>("Email:Provider");
if (string.Equals(emailProvider, "GmailSmtp", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(emailProvider, "Smtp", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<HydroPilotWeb.Services.Notifications.IEmailSender, HydroPilotWeb.Services.Notifications.SmtpEmailSender>();
}
else
{
    builder.Services.AddScoped<HydroPilotWeb.Services.Notifications.IEmailSender, HydroPilotWeb.Services.Notifications.LoggingEmailSender>();
}
builder.Services.AddScoped<HydroPilotWeb.Services.Notifications.NotificationService>();
builder.Services.AddHostedService<HydroPilotWeb.Services.Notifications.NotificationDispatcherHostedService>();

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

builder.Services.AddControllers();

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

app.MapControllers();

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
