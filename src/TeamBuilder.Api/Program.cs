using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamBuilder.Api.Auth;
using TeamBuilder.Api.Errors;
using TeamBuilder.Api.Hosting;
using TeamBuilder.Api.Middleware;
using TeamBuilder.Api.Networking;
using TeamBuilder.Api.Operations;
using TeamBuilder.Api.RateLimiting;
using TeamBuilder.Api.Workers;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Outbox;
using TeamBuilder.Infrastructure.Services;
using TeamBuilder.Infrastructure.WebPush;

// Container health probe (`dotnet TeamBuilder.Api.dll healthcheck [live|ready]`): the runtime
// image has no shell or curl, so HEALTHCHECK calls the API over loopback with this.
if (args.Length > 0 && args[0] == HealthProbeCommand.Name)
{
    Environment.ExitCode = await HealthProbeCommand.RunAsync(args[1..], Console.Out);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Operator maintenance (`dotnet TeamBuilder.Api.dll outbox ...` / `database ...`): runs one
// command against the configured database and exits without starting the web host.
if (args.Length > 0 && args[0] == OutboxCommand.Name)
{
    Environment.ExitCode = await OutboxCommand.RunAsync(args[1..], Console.Out, builder.Configuration);
    return;
}
if (args.Length > 0 && args[0] == DatabaseCommand.Name)
{
    Environment.ExitCode = await DatabaseCommand.RunAsync(args[1..], Console.Out, builder.Configuration);
    return;
}

// Deployment safety: graceful shutdown on SIGTERM, and the database environment stamp that
// keeps background workers (and readiness) off another environment's database.
builder.Services.AddOptions<DeploymentOptions>().BindConfiguration(DeploymentOptions.SectionName);
builder.Services.AddOptions<HostOptions>()
    .Configure<IOptions<DeploymentOptions>>((host, deployment) => host.ShutdownTimeout = deployment.Value.ShutdownTimeout);
builder.Services.AddSingleton<DatabaseEnvironmentGuard>();
builder.Services.AddSingleton<IWorkerStartGate>(sp => sp.GetRequiredService<DatabaseEnvironmentGuard>());

// Add DbContext
builder.Services.AddDbContext<TeamBuilderDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("TeamBuilderSql");
    options.UseSqlServer(connectionString);
});

// Add application services
builder.Services.AddScoped<ITeamService, TeamService>();
builder.Services.AddScoped<IPlayerService, PlayerService>();
builder.Services.AddScoped<IEventService, EventService>();
builder.Services.AddScoped<IEventSeriesService, EventSeriesService>();
builder.Services.AddScoped<IEventRosterService, EventRosterService>();
builder.Services.AddScoped<IEventSeriesMaterializer, EventSeriesMaterializer>();
builder.Services.AddScoped<IVenueService, VenueService>();
builder.Services.AddScoped<IOccurrenceDiscoveryService, OccurrenceDiscoveryService>();
builder.Services.AddSingleton(TimeProvider.System);

// Keeps active recurring series materialized 21 local days ahead (hourly; first pass at startup).
builder.Services.AddOptions<EventSeriesMaterializationOptions>()
    .BindConfiguration(EventSeriesMaterializationOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHostedService<EventSeriesMaterializationWorker>();
builder.Services.AddScoped<IRosterSubscriptionService, RosterSubscriptionService>();
builder.Services.AddScoped<IInAppNotificationService, InAppNotificationService>();

// Transactional outbox: roster mutations insert vacancy messages in their own commit; this
// worker delivers them asynchronously (in-app notifications for "notify me" subscribers).
builder.Services.AddOptions<OutboxOptions>()
    .BindConfiguration(OutboxOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<OutboxProcessor>();
builder.Services.AddScoped<IOutboxMessageHandler, RosterVacancyNotificationHandler>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddOptions<RefillLimitsOptions>()
    .BindConfiguration(RefillLimitsOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Retention of finished refill rows (completed outbox messages, finished push deliveries,
// dead push subscriptions) in bounded batches; Failed messages stay for `outbox replay`.
builder.Services.AddOptions<OutboxMaintenanceOptions>()
    .BindConfiguration(OutboxMaintenanceOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<OutboxMaintenance>();
builder.Services.AddScoped<OutboxOperations>();
builder.Services.AddHostedService<OutboxMaintenanceWorker>();

// Web Push (VAPID): best-effort delivery of the same alerts to registered browsers. Off unless
// WebPush:Enabled with a VAPID key pair from secret configuration; in-app notifications never
// depend on it.
builder.Services.AddOptions<WebPushOptions>()
    .BindConfiguration(WebPushOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<WebPushOptions>>(
    new WebPushOptionsValidator(builder.Environment.IsDevelopment()));
builder.Services.AddSingleton<PushDeliverySignal>();
builder.Services.AddScoped<IPushSubscriptionService, PushSubscriptionService>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<WebPushOptions>>().Value;
    if (!VapidKeys.TryCreate(options.VapidPublicKey, options.VapidPrivateKey, out var keys, out var error))
        throw new InvalidOperationException(error);
    return new VapidTokenFactory(keys!, options.Subject!, sp.GetRequiredService<TimeProvider>());
});
builder.Services.AddHttpClient<IWebPushClient, HttpWebPushClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Never follow a push service redirect elsewhere; recycle connections so DNS changes apply.
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
    // The default HttpClient logging writes each request URL, and a push endpoint URL is a
    // credential (it embeds the browser's push token). The dispatcher logs ids and the service only.
    .RemoveAllLoggers();
builder.Services.AddSingleton<PushDispatcher>();
builder.Services.AddHostedService<PushDeliveryWorker>();
builder.Services.AddScoped<IJoinRequestService, JoinRequestService>();
builder.Services.AddScoped<IRosterImportService, RosterImportService>();

builder.Services.AddHttpContextAccessor();

// External identity (Issuer + Subject, exact keys) and its resolution to a Player.Id,
// configured by Jwt:ExternalIdentity.
builder.Services.AddOptions<ExternalIdentityOptions>().BindConfiguration(ExternalIdentityOptions.SectionName);
builder.Services.AddScoped<IExternalIdentityAccessor, ClaimsExternalIdentityAccessor>();
builder.Services.AddScoped<IPlayerOnboardingService, PlayerOnboardingService>();
builder.Services.AddScoped<ICurrentPlayerResolver, CurrentPlayerResolver>();

// Add JWT Bearer authentication
// Local development can use `dotnet user-jwts` with the non-secret config in appsettings.Development.json.
// All authenticated endpoints use the validated external identity scheme. The subject is opaque
// and maps to an internal player only where ICurrentPlayerResolver is used.
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = ExternalIdentityAuthentication.SchemeName;
        options.DefaultChallengeScheme = ExternalIdentityAuthentication.SchemeName;
        options.DefaultScheme = ExternalIdentityAuthentication.SchemeName;
    })
    .AddJwtBearer(ExternalIdentityAuthentication.SchemeName);
builder.Services.AddAuthorization();

// Configure JWT Bearer options via IConfigureOptions so that test overrides via
// ConfigureAppConfiguration are read at options resolution time, not registration time.
builder.Services.AddOptions<JwtBearerOptions>(ExternalIdentityAuthentication.SchemeName)
    .Configure<IConfiguration, IOptions<ExternalIdentityOptions>>((options, config, externalIdentityOptions) =>
    {
        ConfigureJwtBearer(options, config);

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (!ExternalIdentityAuthentication.TryResolve(
                        context.Principal, externalIdentityOptions.Value, out _, out var error))
                {
                    context.Fail(error!);
                }

                return Task.CompletedTask;
            }
        };
    });

// Public discovery is the first high-frequency anonymous route: a conservative in-memory
// fixed window per client IP (RateLimiting:Discovery). Only the discovery endpoint opts in; it
// is never an authority for roster claims, which the database guards.
builder.Services.AddOptions<DiscoveryRateLimitOptions>()
    .BindConfiguration(DiscoveryRateLimitOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<SubscriptionRateLimitOptions>()
    .BindConfiguration(SubscriptionRateLimitOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<PushSubscriptionRateLimitOptions>()
    .BindConfiguration(PushSubscriptionRateLimitOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Client address behind a reverse proxy: only listed proxies may set X-Forwarded-For (see
// ForwardedHeadersSettings). Off by default, so a direct client's header is ignored.
var forwardedHeaders = builder.Configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
forwardedHeaders.Apply(new ForwardedHeadersOptions()); // fail at startup, not on the first request
builder.Services.Configure<ForwardedHeadersOptions>(forwardedHeaders.Apply);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName;
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too Many Requests",
            Detail = policy is SubscriptionRateLimitOptions.PolicyName or PushSubscriptionRateLimitOptions.PolicyName
                ? "Too many notification changes. Wait a moment and try again."
                : "Too many searches. Wait a moment and try again."
        }, cancellationToken);
    };
    // Authenticated mutation routes partition by issuer|subject (preferred), the client IP only
    // as a fallback; each policy keeps its own windows.
    options.AddPolicy(SubscriptionRateLimitOptions.PolicyName, httpContext =>
    {
        var limits = httpContext.RequestServices.GetRequiredService<IOptions<SubscriptionRateLimitOptions>>().Value;
        return CallerFixedWindow(httpContext, limits.PermitLimit, limits.WindowSeconds);
    });
    options.AddPolicy(PushSubscriptionRateLimitOptions.PolicyName, httpContext =>
    {
        var limits = httpContext.RequestServices.GetRequiredService<IOptions<PushSubscriptionRateLimitOptions>>().Value;
        return CallerFixedWindow(httpContext, limits.PermitLimit, limits.WindowSeconds);
    });
    options.AddPolicy(DiscoveryRateLimitOptions.PolicyName, httpContext =>
    {
        var limits = httpContext.RequestServices.GetRequiredService<IOptions<DiscoveryRateLimitOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.PermitLimit,
                Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

// Add ProblemDetails support
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Add controllers
builder.Services.AddControllers();

// Add API versioning
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS: exact origins from AllowedOrigins (comma-separated). The API authenticates with bearer
// tokens, never cookies, so credentials are not allowed. '*' (anonymous, any origin) is only
// accepted in Development; deployed environments require exact https origins (validated below).
const string CorsPolicyName = "WebClients";
var allowedOrigins = DeploymentConfigurationValidator.ParseOrigins(builder.Configuration["AllowedOrigins"]);
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
    {
        if (allowedOrigins.Contains("*"))
            policy.AllowAnyOrigin();
        else
            policy.WithOrigins(allowedOrigins);
        policy.AllowAnyMethod()
              .WithHeaders("Authorization", "Content-Type", "Accept", "X-Request-Id")
              .WithExposedHeaders("X-Request-Id", "Retry-After", "Location");
    });
});

// Health: /healthz/live (process only) and /healthz/ready (database schema, environment stamp,
// configuration). Web Push is never part of readiness. See HealthEndpoints.
builder.Services.AddTeamBuilderHealthChecks();

builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(180));

var app = builder.Build();

// Fail closed before serving anything: deployed environments (QA, Production, any unknown
// name) need real OIDC, exact https CORS origins, an injected connection string and no
// startup migration. See DeploymentConfigurationValidator and docs/qa/private-qa-deployment.md.
var deploymentErrors = DeploymentConfigurationValidator.Validate(app.Configuration, app.Environment);
if (deploymentErrors.Count > 0)
{
    throw new InvalidOperationException(
        $"Refusing to start in {app.Environment.EnvironmentName}: " + string.Join(" ", deploymentErrors));
}

// Opt-in for Development and LocalQA only (the validator above rejects it anywhere else).
// Deployed environments apply the EF Core migration bundle before the API rolls out, so
// application replicas never race each other on the schema.
if (app.Configuration.GetValue<bool>(DeploymentConfigurationValidator.ApplyMigrationsOnStartupKey))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<TeamBuilderDbContext>().Database.Migrate();
}

// Configure the HTTP request pipeline
// First, so logging and rate limiting see the client address a trusted proxy reported.
app.UseForwardedHeaders();
app.UseExceptionHandler();

// Correlation ID and structured request logging � runs early so every request is covered.
app.UseMiddleware<RequestLoggingMiddleware>();

// Swagger is a Development convenience; elsewhere only with an explicit Swagger:Enabled=true.
if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "TeamBuilder API v1");
    });
}

// HTTPS: deployed environments sit behind a TLS-terminating proxy or ingress. Redirecting is
// only safe when the proxy's X-Forwarded-Proto is trusted (ForwardedHeaders), or every proxied
// request would look like plain HTTP and loop; without it the ingress owns the redirect.
// Health probes arrive over plain HTTP from the orchestrator and are never redirected.
var isDeployed = DeploymentEnvironments.IsDeployed(app.Environment);
var httpsRedirection = app.Configuration.GetValue(
    "Security:HttpsRedirection",
    app.Environment.IsDevelopment() || (isDeployed && forwardedHeaders.Enabled));
if (isDeployed)
    app.UseHsts();
if (httpsRedirection)
{
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/healthz") && !context.Request.Path.StartsWithSegments("/health"),
        branch => branch.UseHttpsRedirection());
}
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseCors(CorsPolicyName);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/", () => Results.Ok("TeamBuilder API Running"));
app.MapControllers();
app.MapTeamBuilderHealthEndpoints();

app.Run();

static RateLimitPartition<string> CallerFixedWindow(HttpContext httpContext, int permitLimit, int windowSeconds)
{
    var identity = httpContext.RequestServices.GetRequiredService<IExternalIdentityAccessor>().Current;
    var partition = identity is null
        ? $"ip:{httpContext.Connection.RemoteIpAddress}"
        : $"id:{identity.Issuer}|{identity.Subject}";
    return RateLimitPartition.GetFixedWindowLimiter(
        partition,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
}

// Token validation shared by both JWT bearer schemes.
static void ConfigureJwtBearer(JwtBearerOptions options, IConfiguration config)
{
    var jwtSection = config.GetSection("Jwt");
    var jwtSigningKey = jwtSection["SigningKey"];
    var jwtAuthority  = jwtSection["Authority"];
    var jwtIssuer     = jwtSection["Issuer"];
    var jwtAudience   = jwtSection["Audience"];

    if (!string.IsNullOrWhiteSpace(jwtSigningKey))
    {
        // Symmetric key path: used for local development (dotnet user-jwts) and tests.
        // No OIDC metadata discovery; Authority is intentionally not set.
        // MapInboundClaims = false preserves the configured external subject claim name.
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(jwtSigningKey)),
            ValidateIssuer    = !string.IsNullOrWhiteSpace(jwtIssuer),
            ValidIssuer       = jwtIssuer,
            ValidateAudience  = !string.IsNullOrWhiteSpace(jwtAudience),
            ValidAudience     = jwtAudience,
            ValidateLifetime  = true,
            ClockSkew         = System.TimeSpan.Zero
        };
    }
    else
    {
        // OIDC authority path: used in staging/production with a real identity provider.
        options.MapInboundClaims                           = false;
        options.Authority              = string.IsNullOrWhiteSpace(jwtAuthority) ? null : jwtAuthority;
        options.Audience               = jwtAudience;
        options.RequireHttpsMetadata   = jwtSection.GetValue("RequireHttpsMetadata", defaultValue: true);
        options.TokenValidationParameters.ValidateAudience = !string.IsNullOrWhiteSpace(jwtAudience);
        options.TokenValidationParameters.ValidateIssuer   = !string.IsNullOrWhiteSpace(jwtAuthority);
    }
}
