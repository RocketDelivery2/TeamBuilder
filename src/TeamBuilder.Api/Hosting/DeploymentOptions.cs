namespace TeamBuilder.Api.Hosting;

/// <summary>Deployment safety settings, section <c>Deployment</c>.</summary>
public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    /// <summary>
    /// The environment name the database must be stamped with (<c>database stamp-environment</c>).
    /// Defaults to <c>ASPNETCORE_ENVIRONMENT</c>, so a QA API refuses a database stamped
    /// Production and the reverse.
    /// </summary>
    public string? DatabaseEnvironment { get; set; }

    /// <summary>
    /// Whether to check the database stamp before background workers start and in readiness.
    /// Defaults to on in deployed environments and off in Development and LocalQA.
    /// </summary>
    public bool? VerifyDatabaseEnvironment { get; set; }

    /// <summary>How long a stopping instance waits for in-flight requests and workers (SIGTERM).</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
