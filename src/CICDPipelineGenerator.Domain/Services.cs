using System.Text.Json;

namespace CICDPipelineGenerator.Domain;

/// <summary>A database or cache started next to the build, for the tests that need a real one.</summary>
public sealed record ServiceDef(string Name, string Image, int Port, IReadOnlyList<KeyValuePair<string, string>> Env);

/// <summary>
/// The services of the build job and the other versions the tests also run with. The services hold test data only:
/// their passwords are written in the pipeline and open nothing outside the job.
/// </summary>
public static class Services
{
    public static ServiceDef Of(ServiceKind kind) => kind switch
    {
        ServiceKind.Postgres => new("postgres", "postgres:17", 5432, [new("POSTGRES_PASSWORD", "postgres"), new("POSTGRES_DB", "test")]),
        ServiceKind.MySql => new("mysql", "mysql:8.4", 3306, [new("MYSQL_ROOT_PASSWORD", "mysql"), new("MYSQL_DATABASE", "test")]),
        ServiceKind.SqlServer => new("sqlserver", "mcr.microsoft.com/mssql/server:2022-latest", 1433, [new("ACCEPT_EULA", "Y"), new("MSSQL_SA_PASSWORD", "Test-only-1")]),
        ServiceKind.Redis => new("redis", "redis:7", 6379, []),
        _ => new("mongodb", "mongo:8", 27017, []),
    };

    public static bool Applies(PipelineSpec spec) => spec.Build.Services.Count > 0 && !spec.IsInfrastructure;

    public static IReadOnlyList<ServiceDef> All(PipelineSpec spec) => Applies(spec) ? [.. spec.Build.Services.Distinct().Select(Of)] : [];

    /// <summary>What the tests read to find a service: POSTGRES_HOST and POSTGRES_PORT. The host is localhost, or the service's name where the system gives it one.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Env(PipelineSpec spec, bool byName) =>
    [
        .. All(spec).SelectMany(service => new KeyValuePair<string, string>[]
        {
            new(Prefix(service) + "_HOST", byName ? service.Name : "localhost"),
            new(Prefix(service) + "_PORT", service.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        }),
    ];

    private static string Prefix(ServiceDef service) => service.Name.ToUpperInvariant();

    /// <summary>Waits until every service takes connections, so the first test does not find a database still starting.</summary>
    public static string? Wait(PipelineSpec spec, bool byName)
    {
        var services = All(spec);
        return services.Count == 0 ? null : string.Join('\n', services.Select(service =>
        {
            var host = byName ? service.Name : "localhost";
            return $"bash -c 'for i in $(seq 1 30); do (echo > /dev/tcp/{host}/{service.Port}) 2>/dev/null && exit 0; sleep 2; done; echo \"{service.Name} did not answer on {host}:{service.Port}\"; exit 1'";
        }));
    }

    public const string WaitName = "Wait for the services";

    /// <summary>Jenkins: the agent's own Docker starts them; one left by a run that failed is removed first.</summary>
    public static string Start(PipelineSpec spec) => string.Join('\n', All(spec).Select(service =>
        $"docker rm -f {spec.Name}-{service.Name} > /dev/null 2>&1 || true\ndocker run -d --name {spec.Name}-{service.Name} -p {service.Port}:{service.Port}{string.Concat(service.Env.Select(pair => $" -e {pair.Key}={pair.Value}"))} {service.Image} > /dev/null"));

    public static string Stop(PipelineSpec spec) => string.Join('\n', All(spec).Select(service => $"docker rm -f {spec.Name}-{service.Name} > /dev/null 2>&1 || true"));

    // ---------------------------------------------------------------- other versions

    public static bool TestsVersions(PipelineSpec spec) =>
        spec.Build.AlsoTestWith.Count > 0 && !spec.Build.TestsOnly && !spec.IsInfrastructure && spec.Build.Stack != Stack.None && spec.Build.CustomCommands.Count == 0;

    public static string Id(string version, char separator = '-') => "test" + separator + version.Trim().Replace('.', separator);

    public static string Name(string version) => "Test with " + version.Trim();

    /// <summary>The same build with another version of the language: it compiles and tests, and keeps nothing.</summary>
    public static IReadOnlyList<(string Version, PipelineSpec Spec)> Versions(PipelineSpec spec)
    {
        if (!TestsVersions(spec))
        {
            return [];
        }

        var list = new List<(string, PipelineSpec)>();
        foreach (var version in spec.Build.AlsoTestWith.Select(version => version.Trim()).Distinct())
        {
            var copy = JsonSerializer.Deserialize<PipelineSpec>(JsonSerializer.Serialize(spec, ProposalPrompt.Json), ProposalPrompt.Json)!;
            copy.Build.Version = version;
            copy.Build.TestsOnly = true;
            copy.Build.AlsoTestWith = [];
            copy.Build.PublishTestResults = false;
            copy.Build.Coverage = false;
            copy.Build.Sbom = false;
            copy.Build.SonarProjectKey = null;
            copy.Build.CodeScan = false;
            copy.Build.SecurityScan = false;
            copy.Build.Lint = false;
            copy.Build.AuditDependencies = false;
            list.Add((version, copy));
        }

        return list;
    }
}
