using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamBuilder.Api.Auth;
using TeamBuilder.Api.Errors;
using TeamBuilder.Api.Middleware;
using TeamBuilder.Application.Interfaces;
using TeamBuilder.Infrastructure.Data;
using TeamBuilder.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

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

// Add ProblemDetails support
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Add controllers
builder.Services.AddControllers();

// Add API versioning
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        var allowedOrigins = builder.Configuration.GetValue<string>("AllowedOrigins")?.Split(',') ?? ["*"];

        if (allowedOrigins.Contains("*"))
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        else
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
    });
});

// Add health checks
// /health  � liveness: fast process-level check, no external dependencies
// /health/ready � readiness: verifies external dependencies (database) are reachable
builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("TeamBuilderSql") ?? "",
        name: "TeamBuilderDb",
        tags: ["ready"]);

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseExceptionHandler();

// Correlation ID and structured request logging � runs early so every request is covered.
app.UseMiddleware<RequestLoggingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "TeamBuilder API v1");
    });
}

if (!app.Environment.IsEnvironment("QA"))
{
    app.UseHttpsRedirection();
}

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok("TeamBuilder API Running"));
app.MapControllers();
// Liveness: always returns Healthy as long as the process is running
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();

// Readiness: returns Healthy only when all external dependencies are reachable
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

app.Run();

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
