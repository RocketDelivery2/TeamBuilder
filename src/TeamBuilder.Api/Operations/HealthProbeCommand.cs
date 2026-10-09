using TeamBuilder.Api.Hosting;

namespace TeamBuilder.Api.Operations;

/// <summary>
/// <c>dotnet TeamBuilder.Api.dll healthcheck [live|ready]</c>: probes the API running in the same
/// container over loopback and exits 0 when it answers 200. The runtime image has no shell or
/// curl, so container health checks (Docker HEALTHCHECK, compose) use this.
/// </summary>
public static class HealthProbeCommand
{
    public const string Name = "healthcheck";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        var path = args.Length > 0 && args[0] == "ready" ? HealthEndpoints.ReadyPath : HealthEndpoints.LivePath;
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080")
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "8080";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{port}{path}");
            await output.WriteLineAsync($"{path} {(int)response.StatusCode}");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await output.WriteLineAsync($"{path} unreachable");
            return 1;
        }
    }
}
