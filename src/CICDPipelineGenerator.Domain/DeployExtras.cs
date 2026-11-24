using System.Globalization;

namespace CICDPipelineGenerator.Domain;

/// <summary>A step the generators place around the deploy: its script and the environment variables it needs.</summary>
public sealed record ExtraStep(string Name, string Script, IReadOnlyList<KeyValuePair<string, string>> Env, bool UsesAzure = false);

/// <summary>How each system names and hands over a secret; the value never appears in the YAML.</summary>
public static class Secrets
{
    public const string DbConnection = "DB_CONNECTION";
    public const string DbServer = "DB_SERVER";
    public const string DbName = "DB_NAME";
    public const string DbUser = "DB_USER";
    public const string DbPassword = "DB_PASSWORD";

    /// <summary>
    /// GitHub reads the environment's secret; GitLab the CI/CD variable (scoped to the environment); Azure DevOps has
    /// no environment scope for variables, so the name carries the environment (DB_CONNECTION_PROD).
    /// </summary>
    public static string Reference(Platform platform, EnvironmentSpec environment, string name) => platform switch
    {
        Platform.GitHubActions => $"${{{{ secrets.{name} }}}}",
        Platform.AzureDevOps => $"$({AzureDevOpsName(environment, name)})",
        _ => "$" + name,
    };

    /// <summary>A secret that is the same for every environment (the registry's user and password).</summary>
    public static string Shared(Platform platform, string name) => platform switch
    {
        Platform.GitHubActions => $"${{{{ secrets.{name} }}}}",
        Platform.AzureDevOps => $"$({name})",
        _ => "$" + name,
    };

    public static string AzureDevOpsName(EnvironmentSpec environment, string name) =>
        name + "_" + environment.Name.Replace('-', '_').ToUpperInvariant();

    /// <summary>Every secret this environment's deploy reads, for the setup guide.</summary>
    public static IReadOnlyList<string> Names(PipelineSpec spec, EnvironmentSpec environment)
    {
        var names = new List<string>();
        switch (spec.Deploy.Migrations)
        {
            case MigrationKind.EfCoreBundle:
                names.Add(DbConnection);
                break;
            case MigrationKind.Dacpac:
                names.Add(DbConnection);
                break;
            case MigrationKind.SqlScripts:
                names.AddRange(spec.Deploy.SqlTrustedConnection ? [DbServer, DbName] : [DbServer, DbName, DbUser, DbPassword]);
                break;
        }

        names.AddRange(RemoteDeploy.SecretNames(spec));
        names.AddRange(environment.Settings.Where(setting => !string.IsNullOrWhiteSpace(setting.Secret)).Select(setting => setting.Secret!));
        return names.Distinct(StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// The steps around a deploy that every system shares: the database migrations and the environment's settings.
/// Values and secrets reach the scripts as environment variables of the step, never as text inside a command, so no
/// value can break (or inject into) a command.
/// </summary>
public static class DeployExtras
{
    public const string MigrationsFolder = "out/migrations";

    /// <summary>The settings of an environment travel as CFG_1, CFG_2... in the order they are listed.</summary>
    public static string Variable(int index) => "CFG_" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static IReadOnlyList<KeyValuePair<string, string>> SettingsEnv(EnvironmentSpec environment, Platform platform) =>
        environment.Settings.Select((setting, index) => new KeyValuePair<string, string>(Variable(index), ValueOf(setting, environment, platform))).ToList();

    private static string ValueOf(EnvironmentSetting setting, EnvironmentSpec environment, Platform platform) =>
        string.IsNullOrWhiteSpace(setting.Secret) ? setting.Value ?? "" : Secrets.Reference(platform, environment, setting.Secret);

    /// <summary>App Service and containers on Linux cannot have : in a setting name; __ means the same to .NET.</summary>
    public static string FlatName(string name) => name.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>A setting as an environment variable name for the person's own commands (ConnectionStrings:Default → ConnectionStrings__Default).</summary>
    public static string EnvName(string name) => FlatName(name).Replace('.', '_').Replace('-', '_');

    /// <summary>The EF Core bundle is built with the code, for the system the deploy job runs on.</summary>
    public static ExtraStep? BuildMigrations(PipelineSpec spec)
    {
        if (spec.Deploy.Migrations != MigrationKind.EfCoreBundle || spec.Build.TestsOnly)
        {
            return null;
        }

        var runtime = spec.UsesPowerShell ? "win-x64" : "linux-x64";
        var bundle = $"dotnet ef migrations bundle --project {spec.Deploy.MigrationsProject} --startup-project {spec.Build.ProjectPath ?? spec.Deploy.MigrationsProject} " +
            $"--configuration Release --self-contained -r {runtime} -o {MigrationsFolder}/{BundleName(spec)} --force";
        var script = spec.IsWindowsBuild
            ? "dotnet tool update --global dotnet-ef\n$env:PATH = $env:PATH + ';' + (Join-Path $env:USERPROFILE '.dotnet\\tools')\n" + bundle
            : "dotnet tool update --global dotnet-ef\nexport PATH=\"$PATH:$HOME/.dotnet/tools\"\n" + bundle;
        return new("Build the database migrations", script, []);
    }

    public static string BundleName(PipelineSpec spec) => spec.UsesPowerShell ? "efbundle.exe" : "efbundle";

    /// <summary>The folder the migrations bundle is downloaded to in a deploy job, as each shell writes it.</summary>
    private static string Bundle(PipelineSpec spec, Platform platform) => (platform, spec.UsesPowerShell) switch
    {
        (Platform.AzureDevOps, true) => "(Join-Path $env:PIPELINE_WORKSPACE 'migrations\\efbundle.exe')",
        (Platform.AzureDevOps, false) => "\"$PIPELINE_WORKSPACE/migrations/efbundle\"",
        (Platform.GitHubActions, true) => "(Join-Path $env:GITHUB_WORKSPACE 'out\\migrations\\efbundle.exe')",
        (Platform.GitLab, true) => "(Join-Path $env:CI_PROJECT_DIR 'out\\migrations\\efbundle.exe')",
        (_, true) => "(Join-Path (Get-Location) 'out\\migrations\\efbundle.exe')",
        _ => MigrationsFolder + "/efbundle",
    };

    /// <summary>Brings the environment's database up to date, before its deploy.</summary>
    public static ExtraStep? Migrate(PipelineSpec spec, EnvironmentSpec environment, Platform platform)
    {
        var deploy = spec.Deploy;
        string Secret(string name) => Secrets.Reference(platform, environment, name);
        switch (deploy.Migrations)
        {
            case MigrationKind.EfCoreBundle:
            {
                var bundle = Bundle(spec, platform);
                var script = spec.UsesPowerShell
                    ? $"& {bundle} --connection $env:DB_CONNECTION\nif ($LASTEXITCODE -ne 0) {{ throw 'The database migration failed.' }}"
                    // The bundle carries its own .NET; it needs no globalization data, which some deploy images lack.
                    : $"chmod +x {bundle}\nDOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 {bundle} --connection \"$DB_CONNECTION\"";
                return new("Update the database", script, [new("DB_CONNECTION", Secret(Secrets.DbConnection))]);
            }

            case MigrationKind.Dacpac:
            {
                var script = spec.UsesPowerShell
                    ? $"$dacpac = Join-Path ({WindowsDeploy.Source(platform)}) {WindowsDeploy.Quote(deploy.DacpacPath!.Replace('/', '\\'))}\n" +
                      "& sqlpackage /Action:Publish ('/SourceFile:' + $dacpac) ('/TargetConnectionString:' + $env:DB_CONNECTION)\n" +
                      "if ($LASTEXITCODE -ne 0) { throw 'Publishing the dacpac failed.' }"
                    : $"sqlpackage /Action:Publish \"/SourceFile:{(platform == Platform.AzureDevOps ? "$PIPELINE_WORKSPACE/app" : Commands.PackageFolder)}/{deploy.DacpacPath}\" \"/TargetConnectionString:$DB_CONNECTION\"";
                return new("Update the database", script, [new("DB_CONNECTION", Secret(Secrets.DbConnection))]);
            }

            case MigrationKind.SqlScripts:
            {
                // sqlcmd reads the server, the database and the login from these variables.
                var env = new List<KeyValuePair<string, string>>
                {
                    new("SQLCMDSERVER", Secret(Secrets.DbServer)),
                    new("SQLCMDDBNAME", Secret(Secrets.DbName)),
                };
                if (!deploy.SqlTrustedConnection)
                {
                    env.Add(new("SQLCMDUSER", Secret(Secrets.DbUser)));
                    env.Add(new("SQLCMDPASSWORD", Secret(Secrets.DbPassword)));
                }

                var script = spec.UsesPowerShell
                    ? string.Join('\n',
                    [
                        $"$scripts = Get-ChildItem -Path {WindowsDeploy.Quote(deploy.MigrationsFolder.Replace('/', '\\'))} -Filter '*.sql' | Sort-Object Name",
                        "foreach ($script in $scripts) {",
                        "    Write-Host ('Running ' + $script.Name)",
                        deploy.SqlTrustedConnection ? "    & sqlcmd -E -b -i $script.FullName" : "    & sqlcmd -b -i $script.FullName",
                        "    if ($LASTEXITCODE -ne 0) { throw ('The script ' + $script.Name + ' failed.') }",
                        "}",
                    ])
                    : string.Join('\n',
                    [
                        $"find {deploy.MigrationsFolder} -maxdepth 1 -name '*.sql' | sort | while read -r file; do",
                        "  echo \"Running $file\"",
                        "  sqlcmd -b -i \"$file\" || exit 1",
                        "done",
                    ]);
                return new("Update the database", script, env);
            }

            default:
                return null;
        }
    }

    /// <summary>The settings as their own step: Azure app settings, or entries of the settings file on a server.</summary>
    public static ExtraStep? ApplySettings(PipelineSpec spec, EnvironmentSpec environment, Platform platform)
    {
        if (environment.Settings.Count == 0)
        {
            return null;
        }

        var env = SettingsEnv(environment, platform);
        switch (spec.Deploy.Target)
        {
            case Target.AppService:
            case Target.FunctionApp:
            {
                var group = spec.Deploy.Target == Target.AppService ? "webapp" : "functionapp";
                var slot = string.IsNullOrWhiteSpace(environment.Slot) ? "" : $" --slot {environment.Slot}";
                var pairs = string.Join(" ", environment.Settings.Select((setting, index) => $"\"{FlatName(setting.Name)}=${Variable(index)}\""));
                return new(
                    "Apply the settings",
                    $"az {group} config appsettings set --resource-group {environment.ResourceGroup} --name {environment.ResourceName}{slot} --output none --settings {pairs}",
                    env,
                    UsesAzure: true);
            }

            case Target.LinuxService:
                return new("Apply the settings", LinuxDeploy.SettingsScript(spec, environment, platform), env);
            case Target.WindowsService:
            case Target.Iis:
            case Target.ScheduledTask:
                return new("Apply the settings", WindowsDeploy.SettingsScript(spec, environment, platform), env);
            default:
                return null;
        }
    }

    /// <summary>Container Apps take the settings in the same update as the image, so there is one new revision.</summary>
    public static string ContainerSettings(EnvironmentSpec environment) =>
        environment.Settings.Count == 0
            ? ""
            : " --set-env-vars " + string.Join(" ", environment.Settings.Select((setting, index) => $"\"{FlatName(setting.Name)}=${Variable(index)}\""));

    /// <summary>The person's own deploy commands get the settings as environment variables named after them.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ScriptEnv(EnvironmentSpec environment, Platform platform) =>
        environment.Settings.Select(setting => new KeyValuePair<string, string>(EnvName(setting.Name), ValueOf(setting, environment, platform))).ToList();

    /// <summary>The variables the deploy step itself needs (Container Apps and your own commands).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> DeployEnv(PipelineSpec spec, EnvironmentSpec environment, Platform platform) =>
        spec.Deploy.Target switch
        {
            Target.ContainerApp => SettingsEnv(environment, platform),
            Target.Script => ScriptEnv(environment, platform),
            _ => RemoteDeploy.Env(spec, environment, platform),
        };

    /// <summary>Stops the deploy outside the days and hours (UTC) the environment may change in.</summary>
    public static ExtraStep? Window(PipelineSpec spec, EnvironmentSpec environment)
    {
        if (string.IsNullOrWhiteSpace(environment.DeployWindow))
        {
            return null;
        }

        // "1-5 08-18": Monday (1) to Friday (5), from 08:00 until 18:00.
        var parts = environment.DeployWindow.Split(' ', '-');
        var (firstDay, lastDay, from, until) = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture));
        var message = $"{environment.Name} only changes on days {firstDay} to {lastDay} of the week, from {from:00}:00 to {until:00}:00 UTC.";
        var script = spec.UsesPowerShell
            ? $"$now = [DateTime]::UtcNow\n$day = [int]$now.DayOfWeek; if ($day -eq 0) {{ $day = 7 }}\nif ($day -lt {firstDay} -or $day -gt {lastDay} -or $now.Hour -lt {from} -or $now.Hour -ge {until}) {{ throw '{message}' }}"
            : $"day=$(date -u +%u); hour=$((10#$(date -u +%H)))\nif [ \"$day\" -lt {firstDay} ] || [ \"$day\" -gt {lastDay} ] || [ \"$hour\" -lt {from} ] || [ \"$hour\" -ge {until} ]; then echo \"{message}\"; exit 1; fi";
        return new("Check the deploy window", script, []);
    }

    /// <summary>Migrations and settings, in the order they run before the deploy step.</summary>
    public static IReadOnlyList<ExtraStep> Before(PipelineSpec spec, EnvironmentSpec environment, Platform platform)
    {
        var steps = new List<ExtraStep>();
        if (Window(spec, environment) is { } window)
        {
            steps.Add(window);
        }

        if (Migrate(spec, environment, platform) is { } migrate)
        {
            steps.Add(migrate);
        }

        if (ApplySettings(spec, environment, platform) is { } settings)
        {
            steps.Add(settings);
        }

        return steps;
    }
}
