using System.Reflection;

namespace TeamBuilder.Api.Hosting;

/// <summary>
/// The version and source commit stamped into the API assembly at build time
/// (<c>-p:Version=… -p:SourceRevisionId=…</c>; the Dockerfile passes its build arguments). The
/// informational version reads <c>{version}+{commit}</c>.
/// </summary>
public static class BuildInfo
{
    private static readonly string Informational =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Version { get; } = Informational.Split('+')[0];

    public static string Commit { get; } = Informational.Contains('+') ? Informational[(Informational.IndexOf('+') + 1)..] : "unknown";
}
