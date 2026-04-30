using Microsoft.EntityFrameworkCore;
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
builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("TeamBuilderSql") ?? "",
        name: "TeamBuilderDb");

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "TeamBuilder API v1");
    });
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
