namespace TeamBuilder.Api.Hosting;

/// <summary>
/// The configuration rules a deployed environment (QA, Production, any unknown name) must meet
/// before the API serves a request. <see cref="Validate"/> returns every violation so one
/// failed start names them all; Program.cs refuses to start on any. Local environments
/// (Development, LocalQA) keep their developer conveniences and are only checked for the
/// rules that hold everywhere.
/// </summary>
public static class DeploymentConfigurationValidator
{
    public const string ApplyMigrationsOnStartupKey = "Database:ApplyMigrationsOnStartup";

    /// <summary>Returns the violations for <paramref name="environment"/>; empty when valid.</summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var errors = new List<string>();
        var origins = ParseOrigins(configuration["AllowedOrigins"]);

        // Everywhere: a wildcard origin never comes with anything but anonymous, credential-free
        // CORS, and only on a developer machine.
        if (origins.Contains("*") && !environment.IsDevelopment())
            errors.Add("AllowedOrigins must list exact origins; '*' is only allowed in Development.");

        if (!DeploymentEnvironments.IsDeployed(environment))
            return errors;

        var name = environment.EnvironmentName;

        if (configuration.GetValue<bool>(ApplyMigrationsOnStartupKey))
            errors.Add($"{ApplyMigrationsOnStartupKey} must be false in {name}: apply the EF Core migration bundle before deploying the API (docs/qa/private-qa-deployment.md).");

        var connectionString = configuration.GetConnectionString("TeamBuilderSql");
        if (string.IsNullOrWhiteSpace(connectionString))
            errors.Add("ConnectionStrings:TeamBuilderSql is required.");
        else if (IsPlaceholder(connectionString) || connectionString.Contains("(localdb)", StringComparison.OrdinalIgnoreCase))
            errors.Add("ConnectionStrings:TeamBuilderSql still holds a placeholder or LocalDB value; inject the environment's connection string.");

        if (!string.IsNullOrWhiteSpace(configuration["Jwt:SigningKey"]))
            errors.Add($"Jwt:SigningKey must not be set in {name}: developer tokens are for Development and LocalQA only. Configure Jwt:Authority for the OIDC provider.");

        var authority = configuration["Jwt:Authority"];
        if (string.IsNullOrWhiteSpace(authority) || IsPlaceholder(authority) ||
            !Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) || authorityUri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Jwt:Authority must be the OIDC provider's https:// authority (issuer) URL.");
        }

        var audience = configuration["Jwt:Audience"];
        if (string.IsNullOrWhiteSpace(audience) || IsPlaceholder(audience))
            errors.Add("Jwt:Audience must be the API audience (client ID or application ID URI) the provider puts in access tokens.");

        if (!configuration.GetValue("Jwt:RequireHttpsMetadata", defaultValue: true))
            errors.Add("Jwt:RequireHttpsMetadata must stay true outside local environments.");

        foreach (var origin in origins)
        {
            if (origin == "*")
                continue; // reported above
            if (IsPlaceholder(origin) || !IsExactHttpsOrigin(origin))
                errors.Add($"AllowedOrigins entry '{Truncate(origin)}' must be an exact https://host[:port] origin with no path.");
        }

        var databaseEnvironment = configuration[$"{DeploymentOptions.SectionName}:DatabaseEnvironment"];
        if (databaseEnvironment is not null && (string.IsNullOrWhiteSpace(databaseEnvironment) || IsPlaceholder(databaseEnvironment)))
            errors.Add("Deployment:DatabaseEnvironment, when set, must name the environment the database is stamped for.");

        return errors;
    }

    /// <summary>Comma-separated origins, trimmed, empty entries dropped.</summary>
    public static string[] ParseOrigins(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsExactHttpsOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/" && !origin.EndsWith('/') &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    // Octopus-style "#{Variable}" tokens left in a checked-in appsettings file mean nothing was injected.
    private static bool IsPlaceholder(string value) => value.Contains("#{", StringComparison.Ordinal);

    private static string Truncate(string value) => value.Length <= 80 ? value : value[..80] + "…";
}
