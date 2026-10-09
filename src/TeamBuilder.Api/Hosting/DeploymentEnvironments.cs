namespace TeamBuilder.Api.Hosting;

/// <summary>
/// The environments TeamBuilder knows (<c>ASPNETCORE_ENVIRONMENT</c>):
/// <list type="bullet">
/// <item><c>Development</c>: a developer machine. Developer tokens, Swagger and opt-in startup migrations.</item>
/// <item><c>LocalQA</c>: the local docker-compose QA stack (docker-compose.qa.yml). Developer tokens
/// with a visible banner, plain HTTP on localhost; otherwise production-shaped.</item>
/// <item><c>QA</c> and <c>Production</c>: deployed environments behind HTTPS with a real OIDC
/// provider. Startup fails closed on unsafe or incomplete configuration.</item>
/// </list>
/// Any other name is treated as a deployed environment, so a typo can never relax a rule.
/// </summary>
public static class DeploymentEnvironments
{
    public const string LocalQA = "LocalQA";
    public const string QA = "QA";

    /// <summary>A developer machine or the local compose stack: developer tokens are allowed.</summary>
    public static bool IsLocal(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment(LocalQA);

    /// <summary>QA, Production or any unknown name: real identity, HTTPS, no schema changes at startup.</summary>
    public static bool IsDeployed(IHostEnvironment environment) => !IsLocal(environment);
}
