using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
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

// Add user context (reads claims from authenticated principal; falls back to X-User-Id header during transition)
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, ClaimsCurrentUserContext>();

// Add JWT Bearer authentication
// Local development: use `dotnet user-jwts` to issue tokens (see docs/auth-plan.md).
// No [Authorize] is required yet; authentication is optional on all existing endpoints.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddAuthorization();

// Configure JWT Bearer options via IConfigureOptions so that test overrides via
// ConfigureAppConfiguration are read at options resolution time, not registration time.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IConfiguration>((options, config) =>
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
            // MapInboundClaims = false preserves raw JWT claim names (e.g. "sub", not the
            // WS-Security URI) so that Jwt:PlayerIdClaim = "sub" resolves correctly.
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
// /health  — liveness: fast process-level check, no external dependencies
// /health/ready — readiness: verifies external dependencies (database) are reachable
builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("TeamBuilderSql") ?? "",
        name: "TeamBuilderDb",
        tags: ["ready"]);

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseExceptionHandler();

// Correlation ID and structured request logging — runs early so every request is covered.
app.UseMiddleware<RequestLoggingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "TeamBuilder API v1");
    });
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
// Liveness: always returns Healthy as long as the process is running
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});

// Readiness: returns Healthy only when all external dependencies are reachable
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();
