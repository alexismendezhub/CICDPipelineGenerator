using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

/// <summary>Several servers, Windows and own build agents, .NET Framework, settings per environment and database migrations.</summary>
public class CoverageTests
{
    private static readonly Platform[] All = [Platform.GitHubActions, Platform.AzureDevOps, Platform.GitLab];

    private static PipelineSpec Example(string name)
    {
        var spec = Examples.Get(name);
        spec.Platforms = [.. All];
        return spec;
    }

    private static GenerationResult Generate(PipelineSpec spec)
    {
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        foreach (var file in result.Files.Where(file => file.Path.EndsWith(".yml", StringComparison.Ordinal)))
        {
            Assert.True(YamlWriterTests.Parse(file.Content).Children.Count > 0, file.Path);
        }

        GenerationTests.AssertActionlint(Content(result, GitHubGenerator.Path));
        SchemaTests.AssertValid(result);
        return result;
    }

    private static string Content(GenerationResult result, string path) => result.Files.Single(file => file.Path == path).Content;

    [Fact]
    public void Several_servers_are_updated_one_after_another_on_every_system()
    {
        var spec = Example("windows-service");
        spec.Environments[2].Servers = ["app-prod-1", "app-prod-2"];
        var result = Generate(spec);

        var prod = Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "deploy-prod");
        var strategy = Map(prod, "strategy");
        Assert.Equal("1", Text(strategy, "max-parallel"));
        Assert.Equal(["app-prod-1", "app-prod-2"], Seq(Map(strategy, "matrix"), "server"));
        Assert.Equal(["self-hosted", "windows", "${{ matrix.server }}"], Seq(prod, "runs-on"));
        Assert.Equal("billing-worker-prod-${{ matrix.server }}", Text(Map(prod, "concurrency"), "group"));

        // The other environments keep one job on their label.
        var dev = Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "deploy-dev");
        Assert.False(dev.Children.ContainsKey(new YamlScalarNode("strategy")));

        var gitlab = Map(YamlWriterTests.Parse(Content(result, GitLabGenerator.Path)), "deploy-prod");
        Assert.Equal(["$SERVER"], Seq(gitlab, "tags"));
        var matrix = (YamlMappingNode)((YamlSequenceNode)Map(gitlab, "parallel").Children[new YamlScalarNode("matrix")]).Children[0];
        Assert.Equal(["app-prod-1", "app-prod-2"], Seq(matrix, "SERVER"));
        Assert.Equal("prod", Text(gitlab, "resource_group"));

        Assert.Contains("maxParallel: '1'", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);

        spec.Environments[2].Servers = ["same", "same"];
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("each server is one runner label", StringComparison.Ordinal));
        var azure = Example("dotnet-app-service");
        azure.Environments[0].Servers = ["a"];
        Assert.Contains(PipelineRules.Check(azure), message => message.Text.Contains("only for deploys that run on your servers", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BuildAgent.HostedWindows)]
    [InlineData(BuildAgent.SelfHostedWindows)]
    [InlineData(BuildAgent.SelfHostedLinux)]
    public void The_build_runs_on_the_chosen_agent(BuildAgent agent)
    {
        var spec = Example("dotnet-app-service");
        spec.Build.Agent = agent;
        spec.Build.AgentLabel = agent == BuildAgent.HostedWindows ? null : "build-pool";
        var result = Generate(spec);
        var windows = agent != BuildAgent.SelfHostedLinux;

        var build = Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "build");
        if (agent == BuildAgent.HostedWindows)
        {
            Assert.Equal("windows-latest", Text(build, "runs-on"));
        }
        else
        {
            Assert.Equal(["self-hosted", windows ? "windows" : "linux", "build-pool"], Seq(build, "runs-on"));
        }

        var restore = Steps(build).Single(step => Text(step, "name") == "Restore");
        Assert.Equal(windows ? "powershell" : null, Text(restore, "shell"));

        var azure = YamlWriterTests.Parse(Content(result, AzureDevOpsGenerator.Path));
        var job = (YamlMappingNode)((YamlSequenceNode)((YamlMappingNode)((YamlSequenceNode)azure.Children[new YamlScalarNode("stages")]).Children[0]).Children[new YamlScalarNode("jobs")]).Children[0];
        var pool = Map(job, "pool");
        Assert.Equal(agent == BuildAgent.HostedWindows ? "windows-latest" : "build-pool", Text(pool, agent == BuildAgent.HostedWindows ? "vmImage" : "name"));
        Assert.Equal(windows, Content(result, AzureDevOpsGenerator.Path).Contains("- powershell: dotnet restore MyApp.slnx", StringComparison.Ordinal));

        var gitlab = Map(YamlWriterTests.Parse(Content(result, GitLabGenerator.Path)), "build");
        Assert.Equal([agent == BuildAgent.HostedWindows ? "saas-windows-medium-amd64" : "build-pool"], Seq(gitlab, "tags"));
        Assert.Equal(!windows, gitlab.Children.ContainsKey(new YamlScalarNode("image")));
        Assert.Equal(windows, Seq(gitlab, "script")[0].Contains("cd $env:CI_PROJECT_DIR", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_agents_are_checked()
    {
        var spec = Example("dotnet-app-service");
        spec.Build.Agent = BuildAgent.SelfHostedLinux;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("label of your build agents", StringComparison.Ordinal));

        var node = Example("node-static-web-app");
        node.Build.Agent = BuildAgent.HostedWindows;
        Assert.Contains(PipelineRules.Check(node), message => message.Text.Contains("On a Windows build agent", StringComparison.Ordinal));

        var container = Example("container-app");
        container.Build.Agent = BuildAgent.HostedWindows;
        Assert.Contains(PipelineRules.Check(container), message => message.Text.Contains("built on Linux agents", StringComparison.Ordinal));
    }

    private static PipelineSpec Framework(string example = "iis-site")
    {
        var spec = Example(example);
        spec.Build.Stack = Stack.DotNetFramework;
        spec.Build.Agent = BuildAgent.HostedWindows;
        return spec;
    }

    [Fact]
    public void Net_framework_builds_with_msbuild_and_vstest_on_windows()
    {
        var spec = Framework();
        var result = Generate(spec);
        var github = Content(result, GitHubGenerator.Path);
        Assert.DoesNotContain("actions/setup-dotnet", github, StringComparison.Ordinal);
        Assert.Contains("& $msbuild Portal.slnx -t:Restore -p:RestorePackagesConfig=true", github, StringComparison.Ordinal);
        Assert.Contains("& $vstest $tests", github, StringComparison.Ordinal);
        // An IIS site is a web application: it is published the way Visual Studio does it.
        Assert.Contains("-p:DeployOnBuild=true -p:WebPublishMethod=FileSystem", github, StringComparison.Ordinal);
        Assert.DoesNotContain("UseDotNet@2", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);

        // A Windows service is the build output.
        var service = Commands.Build(Framework("windows-service"));
        Assert.Contains("\"-p:OutDir=$out/\"", service.Single(step => step.Name == "Publish").Script, StringComparison.Ordinal);

        // Azure DevOps would read $( ) as a macro.
        Assert.All(Commands.Build(spec), step => Assert.DoesNotContain("$(", step.Script, StringComparison.Ordinal));
        ServerDeployTests.AssertParses(Commands.Build(spec).Concat(service).Select(step => step.Script));

        spec.Build.Agent = BuildAgent.HostedLinux;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("choose a Windows build agent", StringComparison.Ordinal));
    }

    private static PipelineSpec WithSettings(string example)
    {
        var spec = Example(example);
        spec.Environments[2].Settings =
        [
            new() { Name = "Logging:LogLevel:Default", Value = "Warning" },
            new() { Name = "ConnectionStrings:Default", Secret = "DB_CONNECTION" },
        ];
        return spec;
    }

    [Fact]
    public void App_service_settings_are_set_before_the_deploy_and_never_inside_the_command()
    {
        var result = Generate(WithSettings("dotnet-app-service"));

        var steps = Steps(Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "deploy-prod"));
        var apply = steps.Single(step => Text(step, "name") == "Apply the settings");
        Assert.Equal(steps.Count - 2, steps.IndexOf(apply));
        Assert.Equal(
            "az webapp config appsettings set --resource-group rg-contoso-prod --name app-my-app-prod --output none --settings \"Logging__LogLevel__Default=$CFG_1\" \"ConnectionStrings__Default=$CFG_2\"",
            Text(apply, "run"));
        Assert.Equal("Warning", Text(Map(apply, "env"), "CFG_1"));
        Assert.Equal("${{ secrets.DB_CONNECTION }}", Text(Map(apply, "env"), "CFG_2"));

        var azure = Content(result, AzureDevOpsGenerator.Path);
        Assert.Contains("CFG_2: $(DB_CONNECTION_PROD)", azure, StringComparison.Ordinal);
        Assert.Contains("displayName: Apply the settings", azure, StringComparison.Ordinal);

        var gitlab = Map(YamlWriterTests.Parse(Content(result, GitLabGenerator.Path)), "deploy-prod");
        Assert.Equal("$DB_CONNECTION", Text(Map(gitlab, "variables"), "CFG_2"));
        Assert.Contains("az webapp config appsettings set", Seq(gitlab, "script")[0], StringComparison.Ordinal);

        var guide = Content(result, SetupGuide.Path);
        Assert.Contains("## Secrets of the environments", guide, StringComparison.Ordinal);
        Assert.Contains("`DB_CONNECTION_PROD`", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void Container_apps_get_the_settings_with_the_image_and_scripts_as_variables()
    {
        var container = Generate(WithSettings("container-app"));
        var deploy = Steps(Map(Map(YamlWriterTests.Parse(Content(container, GitHubGenerator.Path)), "jobs"), "deploy-prod")).Last();
        // The update with its settings, and then the wait for the new revision.
        Assert.Contains("--set-env-vars \"Logging__LogLevel__Default=$CFG_1\" \"ConnectionStrings__Default=$CFG_2\" --output none\n", Text(deploy, "run"), StringComparison.Ordinal);
        Assert.Equal("${{ secrets.DB_CONNECTION }}", Text(Map(deploy, "env"), "CFG_2"));

        var script = Generate(WithSettings("script"));
        var own = Steps(Map(Map(YamlWriterTests.Parse(Content(script, GitHubGenerator.Path)), "jobs"), "deploy-prod")).Last();
        Assert.Equal("Warning", Text(Map(own, "env"), "Logging__LogLevel__Default"));
        Assert.Equal("${{ secrets.DB_CONNECTION }}", Text(Map(own, "env"), "ConnectionStrings__Default"));
    }

    [Theory]
    [InlineData("Has$Dollar", null, "one line of at most 500 characters")]
    [InlineData("x", "DB", "has a value and a secret")]
    [InlineData(null, "lowercase", "name in capitals")]
    public void Setting_values_cannot_reach_a_command(string? value, string? secret, string message)
    {
        var spec = Example("dotnet-app-service");
        spec.Environments[0].Settings = [new() { Name = "Key", Value = value, Secret = secret }];
        Assert.Contains(PipelineRules.Check(spec), item => item.Severity == Severity.Error && item.Text.Contains(message, StringComparison.Ordinal));

        spec.Environments[0].Settings = [new() { Name = "Bad name'", Value = "x" }];
        Assert.Contains(PipelineRules.Check(spec), item => item.Text.Contains("a setting name uses", StringComparison.Ordinal));

        var aks = Example("aks");
        aks.Environments[0].Settings = [new() { Name = "Key", Value = "x" }];
        Assert.Contains(PipelineRules.Check(aks), item => item.Text.Contains("settings are for App Service", StringComparison.Ordinal));
    }

    /// <summary>Runs the real settings script in Windows PowerShell on a JSON file and on an XML .config file.</summary>
    [Fact]
    public void On_servers_the_settings_are_written_into_the_settings_file()
    {
        var spec = WithSettings("windows-service");
        var environment = spec.Environments[2];
        var result = Generate(spec);
        var steps = Steps(Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "deploy-prod"));
        Assert.Equal("Apply the settings", Text(steps[^2], "name"));
        Assert.Equal("powershell", Text(steps[^2], "shell"));

        var json = WindowsDeploy.SettingsScript(spec, environment, Platform.GitHubActions);
        spec.Deploy.SettingsFile = "Web.config";
        var xml = WindowsDeploy.SettingsScript(spec, environment, Platform.GitHubActions);
        Assert.DoesNotContain("$(", json, StringComparison.Ordinal);
        ServerDeployTests.AssertParses([json, xml]);

        var powershell = ServerDeployTests.PowerShell();
        if (powershell is null)
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("cicdpipelinegenerator-");
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "out", "app")).FullName;
            File.WriteAllText(Path.Combine(app, "appsettings.json"), """{ "Logging": { "LogLevel": { "Default": "Information" } }, "Other": 1 }""");
            File.WriteAllText(Path.Combine(app, "Web.config"), """
                <configuration>
                  <appSettings><add key="Logging:LogLevel:Default" value="Information" /></appSettings>
                  <connectionStrings><add name="Default" connectionString="old" /></connectionStrings>
                </configuration>
                """);
            var values = new Dictionary<string, string> { ["CFG_1"] = "Warning", ["CFG_2"] = "Server=sql;Database=billing;Password='p\"x'" };
            foreach (var (name, script) in new[] { ("json.ps1", json), ("xml.ps1", xml) })
            {
                var path = Path.Combine(root.FullName, name);
                File.WriteAllText(path, script);
                var (code, output) = ServerDeployTests.Run(powershell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path], root.FullName, values);
                Assert.True(code == 0, output);
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "appsettings.json")));
            Assert.Equal("Warning", document.RootElement.GetProperty("Logging").GetProperty("LogLevel").GetProperty("Default").GetString());
            Assert.Equal(values["CFG_2"], document.RootElement.GetProperty("ConnectionStrings").GetProperty("Default").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("Other").GetInt32());

            var config = System.Xml.Linq.XDocument.Load(Path.Combine(app, "Web.config"));
            Assert.Equal("Warning", config.Root!.Element("appSettings")!.Element("add")!.Attribute("value")!.Value);
            Assert.Equal(values["CFG_2"], config.Root.Element("connectionStrings")!.Element("add")!.Attribute("connectionString")!.Value);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Entity_framework_migrations_are_bundled_with_the_build_and_run_before_each_deploy()
    {
        var spec = Example("dotnet-app-service");
        spec.Deploy.Migrations = MigrationKind.EfCoreBundle;
        spec.Deploy.MigrationsProject = "src/Data/Data.csproj";
        var result = Generate(spec);

        var jobs = Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs");
        var build = Steps(Map(jobs, "build"));
        Assert.Contains("dotnet ef migrations bundle --project src/Data/Data.csproj --startup-project src/MyApp/MyApp.csproj --configuration Release --self-contained -r linux-x64 -o out/migrations/efbundle --force",
            Text(build.Single(step => Text(step, "name") == "Build the database migrations"), "run"), StringComparison.Ordinal);
        Assert.Equal("migrations", Text(Map(build.Last(), "with"), "name"));

        var prod = Steps(Map(jobs, "deploy-prod"));
        Assert.Equal("migrations", Text(Map(prod[0], "with"), "name"));
        var update = prod.Single(step => Text(step, "name") == "Update the database");
        Assert.Equal(prod.Count - 2, prod.IndexOf(update));
        Assert.Contains("out/migrations/efbundle --connection \"$DB_CONNECTION\"", Text(update, "run"), StringComparison.Ordinal);
        Assert.Equal("${{ secrets.DB_CONNECTION }}", Text(Map(update, "env"), "DB_CONNECTION"));

        var azure = Content(result, AzureDevOpsGenerator.Path);
        Assert.Contains("artifact: migrations", azure, StringComparison.Ordinal);
        Assert.Contains("\"$PIPELINE_WORKSPACE/migrations/efbundle\" --connection \"$DB_CONNECTION\"", azure, StringComparison.Ordinal);
        Assert.Contains("DB_CONNECTION: $(DB_CONNECTION_PROD)", azure, StringComparison.Ordinal);

        var gitlab = YamlWriterTests.Parse(Content(result, GitLabGenerator.Path));
        Assert.Equal(["out/app", "out/migrations"], Seq(Map(Map(gitlab, "build"), "artifacts"), "paths"));
        // GitLab already gives the job the CI/CD variable DB_CONNECTION of the environment.
        Assert.False(Map(gitlab, "deploy-prod").Children.ContainsKey(new YamlScalarNode("variables")));

        // On a server the bundle is a Windows program.
        var service = Example("windows-service");
        service.Deploy.Migrations = MigrationKind.EfCoreBundle;
        service.Deploy.MigrationsProject = "src/Data/Data.csproj";
        Assert.Contains("-r win-x64 -o out/migrations/efbundle.exe", Content(Generate(service), GitHubGenerator.Path), StringComparison.Ordinal);
        ServerDeployTests.AssertParses(All.Select(platform => DeployExtras.Migrate(service, service.Environments[0], platform)!.Script));

        spec.Build.Stack = Stack.Node;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("need a .NET build", StringComparison.Ordinal));
    }

    [Fact]
    public void Sql_scripts_run_with_sqlcmd_from_the_repository()
    {
        var spec = Example("dotnet-app-service");
        spec.Deploy.Migrations = MigrationKind.SqlScripts;
        var result = Generate(spec);
        Assert.Contains(result.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("running them again changes nothing", StringComparison.Ordinal));

        var prod = Steps(Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "deploy-prod"));
        Assert.Equal("actions/checkout@v5", Text(prod[0], "uses"));
        var update = prod.Single(step => Text(step, "name") == "Update the database");
        Assert.Contains("find db/migrations -maxdepth 1 -name '*.sql' | sort | while read -r file; do", Text(update, "run"), StringComparison.Ordinal);
        Assert.Equal(["SQLCMDSERVER", "SQLCMDDBNAME", "SQLCMDUSER", "SQLCMDPASSWORD"], Map(update, "env").Children.Keys.Select(key => ((YamlScalarNode)key).Value));
        Assert.Contains("- checkout: self", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        Assert.Equal("$DB_SERVER", Text(Map(Map(YamlWriterTests.Parse(Content(result, GitLabGenerator.Path)), "deploy-prod"), "variables"), "SQLCMDSERVER"));

        // On a server, Windows authentication needs no login.
        var service = Example("windows-service");
        service.Deploy.Migrations = MigrationKind.SqlScripts;
        service.Deploy.SqlTrustedConnection = true;
        var step = DeployExtras.Migrate(service, service.Environments[0], Platform.GitHubActions)!;
        Assert.Contains("& sqlcmd -E -b -i $script.FullName", step.Script, StringComparison.Ordinal);
        Assert.Equal(["SQLCMDSERVER", "SQLCMDDBNAME"], step.Env.Select(pair => pair.Key));
        ServerDeployTests.AssertParses([step.Script]);
        Generate(service);

        spec.Deploy.SqlTrustedConnection = true;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("only for SQL scripts run on your servers", StringComparison.Ordinal));
        var terraform = Example("terraform");
        terraform.Deploy.Migrations = MigrationKind.SqlScripts;
        Assert.Contains(PipelineRules.Check(terraform), message => message.Text.Contains("not with Terraform or Bicep", StringComparison.Ordinal));
    }

    [Fact]
    public void A_schedule_runs_the_pipeline_at_set_times()
    {
        var spec = Example("dotnet-app-service");
        spec.Schedule = "0 6 * * 1-5";
        var result = Generate(spec);

        var on = Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "on");
        Assert.Equal("0 6 * * 1-5", Text((YamlMappingNode)((YamlSequenceNode)on.Children[new YamlScalarNode("schedule")]).Children[0], "cron"));

        var schedule = (YamlMappingNode)((YamlSequenceNode)YamlWriterTests.Parse(Content(result, AzureDevOpsGenerator.Path)).Children[new YamlScalarNode("schedules")]).Children[0];
        Assert.Equal("0 6 * * 1-5", Text(schedule, "cron"));
        Assert.Equal("true", Text(schedule, "always"));

        Assert.Contains("$CI_PIPELINE_SOURCE == \"schedule\"", Content(result, GitLabGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("Pipeline schedules", Content(result, SetupGuide.Path), StringComparison.Ordinal);

        spec.Schedule = "every day";
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("cron expression with five fields", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tag_deploys_and_the_branch_only_builds()
    {
        var spec = Example("dotnet-app-service");
        spec.Flow = Flow.Tag;
        var result = Generate(spec);

        var github = YamlWriterTests.Parse(Content(result, GitHubGenerator.Path));
        Assert.Equal(["v*"], Seq(Map(Map(github, "on"), "push"), "tags"));
        Assert.Equal("github.event_name != 'pull_request' && startsWith(github.ref, 'refs/tags/')", Text(Map(Map(github, "jobs"), "deploy-prod"), "if"));
        Assert.Equal("deploy-test", Text(Map(Map(github, "jobs"), "deploy-prod"), "needs"));

        var azure = Content(result, AzureDevOpsGenerator.Path);
        Assert.Equal(["v*"], Seq(Map(Map(YamlWriterTests.Parse(azure), "trigger"), "tags"), "include"));
        Assert.Contains("startsWith(variables['Build.SourceBranch'], 'refs/tags/')", azure, StringComparison.Ordinal);

        var gitlab = Content(result, GitLabGenerator.Path);
        Assert.Contains("- if: $CI_COMMIT_TAG =~ /^v/", gitlab, StringComparison.Ordinal);
        Assert.Contains(result.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("flexible subject", StringComparison.Ordinal));

        spec.TagPattern = "v* x";
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("tags that deploy", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_run_posts_to_the_webhook()
    {
        var spec = Example("dotnet-app-service");
        spec.NotifyOnFailure = true;
        var result = Generate(spec);

        var notify = Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "notify");
        Assert.Equal(["build", "deploy-dev", "deploy-test", "deploy-prod"], Seq(notify, "needs"));
        Assert.Equal("failure() && github.event_name != 'pull_request'", Text(notify, "if"));
        Assert.Equal("${{ secrets.NOTIFY_WEBHOOK }}", Text(Map(Steps(notify)[0], "env"), "WEBHOOK"));
        Assert.Contains("stage: Notify", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("WEBHOOK: $(NOTIFY_WEBHOOK)", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        Assert.Equal(".post", Text(Map(YamlWriterTests.Parse(Content(result, GitLabGenerator.Path)), "notify-failure"), "stage"));
        Assert.Contains("`NOTIFY_WEBHOOK`", Content(result, SetupGuide.Path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet-app-service")]
    [InlineData("aks")]
    [InlineData("node-static-web-app")]
    public void Test_results_audits_and_the_package_cache_are_written_for_every_system(string example)
    {
        var spec = Example(example);
        var stack = spec.Build.Stack;
        spec.Build.PublishTestResults = stack is Stack.DotNet or Stack.Java;
        spec.Build.AuditDependencies = stack is Stack.DotNet or Stack.Node;
        spec.Build.CachePackages = true;
        var result = Generate(spec);
        var github = Content(result, GitHubGenerator.Path);
        var azure = Content(result, AzureDevOpsGenerator.Path);
        var gitlab = Content(result, GitLabGenerator.Path);

        Assert.Contains("task: Cache@2", azure, StringComparison.Ordinal);
        Assert.Contains("cache:", gitlab, StringComparison.Ordinal);
        switch (stack)
        {
            case Stack.DotNet:
                Assert.Contains("--logger trx --results-directory out/test-results", github, StringComparison.Ordinal);
                Assert.Contains("\"-warnaserror:NU1903;NU1904\"", github, StringComparison.Ordinal);
                Assert.Contains("uses: actions/cache@v5", github, StringComparison.Ordinal);
                Assert.Contains("testResultsFormat: VSTest", azure, StringComparison.Ordinal);
                Assert.Contains("NUGET_PACKAGES: $(Pipeline.Workspace)/.nuget/packages", azure, StringComparison.Ordinal);
                Assert.Contains("- out/test-results", gitlab, StringComparison.Ordinal);
                break;
            case Stack.Java:
                Assert.Contains("testResultsFormat: JUnit", azure, StringComparison.Ordinal);
                Assert.Contains("junit:", gitlab, StringComparison.Ordinal);
                Assert.Contains("-Dmaven.repo.local=$CI_PROJECT_DIR/.m2/repository", gitlab, StringComparison.Ordinal);
                break;
            default:
                Assert.Contains("npm audit --audit-level=high", github, StringComparison.Ordinal);
                Assert.Contains("npm audit --audit-level=high", gitlab, StringComparison.Ordinal);
                break;
        }

        spec.Build.Agent = BuildAgent.SelfHostedLinux;
        spec.Build.AgentLabel = "pool";
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("Keeping the packages between runs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dacpac_of_the_build_is_published_with_sqlpackage()
    {
        var spec = Example("dotnet-app-service");
        spec.Deploy.Migrations = MigrationKind.Dacpac;
        spec.Deploy.DacpacPath = "Database.dacpac";
        var result = Generate(spec);
        Assert.Contains("sqlpackage /Action:Publish \"/SourceFile:out/app/Database.dacpac\" \"/TargetConnectionString:$DB_CONNECTION\"", Content(result, GitHubGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("\"/SourceFile:$PIPELINE_WORKSPACE/app/Database.dacpac\"", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);

        var service = Example("windows-service");
        service.Deploy.Migrations = MigrationKind.Dacpac;
        service.Deploy.DacpacPath = "db/Database.dacpac";
        Generate(service);
        ServerDeployTests.AssertParses(All.Select(platform => DeployExtras.Migrate(service, service.Environments[0], platform)!.Script));

        spec.Deploy.DacpacPath = "Database.sql";
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains(".dacpac file inside the build", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scheduled_task_is_disabled_while_its_folder_changes()
    {
        var spec = Example("windows-service");
        spec.Deploy.Target = Target.ScheduledTask;
        foreach (var environment in spec.Environments)
        {
            environment.ResourceName = "Nightly Import";
        }

        var result = Generate(spec);
        var script = WindowsDeploy.Script(spec, spec.Environments[0], Platform.AzureDevOps);
        Assert.Contains("$name = 'Nightly Import'", script, StringComparison.Ordinal);
        Assert.Contains("Disable-ScheduledTask -TaskName $name", script, StringComparison.Ordinal);
        Assert.Contains("Enable-ScheduledTask -TaskName $name", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$(", script, StringComparison.Ordinal);
        ServerDeployTests.AssertParses([script]);
        Assert.Contains("scheduled task", Content(result, SetupGuide.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void Maven_and_pip_read_private_feeds_and_the_image_build_gets_them_as_secret_arguments()
    {
        var maven = PackageFeedTests.Spec(PackageFeedTests.Feed(FeedKind.JFrog, FeedType.Maven));
        var github = Content(Generate(maven), GitHubGenerator.Path);
        Assert.Contains("cat > \"$HOME/.m2/settings.xml\" <<'XML'", github, StringComparison.Ordinal);
        Assert.Contains("<server><id>company</id><username>${env.FEED_COMPANY_USER}</username><password>${env.FEED_COMPANY_TOKEN}</password></server>", github, StringComparison.Ordinal);
        Assert.Contains("<repository><id>company</id><url>https://acme.jfrog.io/artifactory/nuget-virtual</url></repository>", github, StringComparison.Ordinal);
        Assert.Contains("FEED_COMPANY_TOKEN: ${{ secrets.FEED_COMPANY_TOKEN }}", github, StringComparison.Ordinal);

        var pip = PackageFeedTests.Spec(PackageFeedTests.Feed(FeedKind.AzureArtifacts, FeedType.Pip));
        var result = Generate(pip);
        Assert.Contains("PIP_EXTRA_INDEX_URL: https://azure-artifacts:${{ secrets.FEED_COMPANY_TOKEN }}@pkgs.dev.azure.com/contoso/platform/_packaging/packages/pypi/simple/", Content(result, GitHubGenerator.Path), StringComparison.Ordinal);
        // Azure DevOps reads its own feeds with the build's token.
        Assert.Contains("PIP_EXTRA_INDEX_URL: https://azure-artifacts:$(System.AccessToken)@pkgs.dev.azure.com", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("PIP_EXTRA_INDEX_URL: https://azure-artifacts:$FEED_COMPANY_TOKEN@pkgs.dev.azure.com", Content(result, GitLabGenerator.Path), StringComparison.Ordinal);

        var container = Example("container-app");
        container.Build.PackageFeeds = [PackageFeedTests.Feed(FeedKind.Other, FeedType.NuGet)];
        var image = Content(Generate(container), GitHubGenerator.Path);
        Assert.Contains("--secret-build-arg FEED_COMPANY_USER=\"$FEED_COMPANY_USER\" --secret-build-arg FEED_COMPANY_TOKEN=\"$FEED_COMPANY_TOKEN\"", image, StringComparison.Ordinal);

        var wrong = PackageFeedTests.Spec(PackageFeedTests.Feed(FeedKind.GitHubPackages, FeedType.Pip));
        Assert.Contains(PipelineRules.Check(wrong), message => message.Text.Contains("no Python package index", StringComparison.Ordinal));
    }

    [Fact]
    public void A_package_is_packed_and_published_to_the_feed()
    {
        var spec = GenerationTests.Spec(Target.Package, Stack.DotNet);
        var result = Generate(spec);
        var github = Content(result, GitHubGenerator.Path);
        Assert.Contains("dotnet pack src/MyApp/MyApp.csproj -c Release -o out/app", github, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget push \"out/app/*.nupkg\" --source publish --api-key az --skip-duplicate", github, StringComparison.Ordinal);
        Assert.Contains("NuGetPackageSourceCredentials_publish: Username=${{ vars.FEED_PUBLISH_USER }};Password=${{ secrets.FEED_PUBLISH_TOKEN }}", github, StringComparison.Ordinal);
        Assert.DoesNotContain("azure/login", github, StringComparison.Ordinal);
        Assert.Contains("\"$PIPELINE_WORKSPACE/app/*.nupkg\"", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);

        var npm = GenerationTests.Spec(Target.Package, Stack.Node);
        npm.Deploy.PublishFeed = new() { Kind = FeedKind.GitHubPackages, Type = FeedType.Npm, Organization = "contoso" };
        var workflow = Content(Generate(npm), GitHubGenerator.Path);
        Assert.Contains("npm pack --pack-destination \"$root/out/app\"", workflow, StringComparison.Ordinal);
        Assert.Contains("npm publish \"$package\" --registry \"https://npm.pkg.github.com/\"", workflow, StringComparison.Ordinal);
        Assert.Equal("write", Text(Map(YamlWriterTests.Parse(workflow), "permissions"), "packages"));

        spec.Deploy.PublishFeed = null;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("Choose the feed the package is published to", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rollback_pipeline_puts_the_previous_version_back_on_the_chosen_environment()
    {
        var spec = Example("iis-site");
        spec.Deploy.RollbackPipeline = true;
        spec.Environments[2].Servers = ["web-prod-1", "web-prod-2"];
        var result = Generate(spec);

        var github = Content(result, ".github/workflows/rollback.yml");
        GenerationTests.AssertActionlint(github);
        var root = YamlWriterTests.Parse(github);
        Assert.Equal(["dev", "test", "prod"], Seq(Map(Map(Map(Map(root, "on"), "workflow_dispatch"), "inputs"), "environment"), "options"));
        var prod = Map(Map(root, "jobs"), "rollback-prod");
        Assert.Equal("inputs.environment == 'prod'", Text(prod, "if"));
        var script = Text(Steps(prod)[0], "run")!;
        Assert.Contains("Copy-Folder $backup $target", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Folder $source $target", script, StringComparison.Ordinal);

        var azure = Content(result, "azure-pipelines-rollback.yml");
        Assert.Contains("condition: eq('${{ parameters.environment }}', 'prod')", azure, StringComparison.Ordinal);
        var gitlab = Map(YamlWriterTests.Parse(Content(result, GitLabGenerator.Path)), "rollback-prod");
        Assert.Equal("rollback", Text(gitlab, "stage"));
        Assert.Equal("manual", Text((YamlMappingNode)((YamlSequenceNode)gitlab.Children[new YamlScalarNode("rules")]).Children[0], "when"));
        ServerDeployTests.AssertParses(All.Select(platform => WindowsDeploy.RollbackScript(spec, spec.Environments[0], platform)));

        var app = Example("dotnet-app-service");
        app.Deploy.RollbackPipeline = true;
        Assert.Contains(PipelineRules.Check(app), message => message.Text.Contains("A rollback pipeline is for", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_applications_in_one_repository_get_their_own_files()
    {
        var spec = Example("dotnet-app-service");
        spec.SeveralApps = true;
        spec.PathFilters = ["src/MyApp/**"];
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors);
        Assert.Equal([".github/workflows/deploy-my-app.yml", "azure-pipelines-my-app.yml", "ci/my-app.gitlab-ci.yml", "SETUP-my-app.md"], result.Files.Select(file => file.Path));
        SchemaTests.AssertValid(result);
        GenerationTests.AssertActionlint(result.Files[0].Content);
        Assert.Contains("$CI_PIPELINE_SOURCE == \"parent_pipeline\"", result.Files[2].Content, StringComparison.Ordinal);
        Assert.Contains("include: ci/my-app.gitlab-ci.yml", result.Files[3].Content, StringComparison.Ordinal);

        spec.PathFilters = [];
        Assert.Contains(PipelineRules.Check(spec), message => message.Severity == Severity.Warning && message.Text.Contains("several applications", StringComparison.Ordinal));
    }

    [Fact]
    public void Aws_targets_sign_in_without_stored_keys_where_the_system_allows_it()
    {
        var spec = GenerationTests.Spec(Target.AwsEcs, Stack.DotNet);
        var result = Generate(spec);
        var github = Content(result, GitHubGenerator.Path);
        Assert.Contains("uses: aws-actions/configure-aws-credentials@v6", github, StringComparison.Ordinal);
        Assert.Contains("role-to-assume: ${{ vars.AWS_ROLE_ARN }}", github, StringComparison.Ordinal);
        Assert.Contains("docker push \"$registry/my-app:${{ github.sha }}\"", github, StringComparison.Ordinal);
        Assert.Contains("aws ecs wait services-stable --cluster rg-contoso-prod --services app-my-app-prod", github, StringComparison.Ordinal);
        Assert.DoesNotContain("azure/login", github, StringComparison.Ordinal);

        var azure = Content(result, AzureDevOpsGenerator.Path);
        Assert.Contains("AWS_ACCESS_KEY_ID: $(AWS_ACCESS_KEY_ID_PROD)", azure, StringComparison.Ordinal);
        Assert.DoesNotContain("AzureCLI@2", azure, StringComparison.Ordinal);

        var gitlab = YamlWriterTests.Parse(Content(result, GitLabGenerator.Path));
        Assert.Equal("https://gitlab.com", Text(Map(Map(Map(gitlab, "deploy-prod"), "id_tokens"), "AWS_JWT"), "aud"));
        Assert.Equal("docker:27", Text(Map(gitlab, "build-image"), "image"));
        Assert.Contains("assume-role-with-web-identity", Seq(Map(gitlab, "deploy-prod"), "before_script")[1], StringComparison.Ordinal);
        Assert.Contains("## 1. AWS identity and permissions", Content(result, SetupGuide.Path), StringComparison.Ordinal);

        var site = Content(Generate(GenerationTests.Spec(Target.AwsS3, Stack.Node)), GitHubGenerator.Path);
        Assert.Contains("aws s3 sync \"out/app\" \"s3://app-my-app-prod\" --delete", site, StringComparison.Ordinal);
        var beanstalk = Content(Generate(GenerationTests.Spec(Target.AwsElasticBeanstalk, Stack.DotNet)), AzureDevOpsGenerator.Path);
        Assert.Contains("aws elasticbeanstalk update-environment --environment-name app-my-app-prod", beanstalk, StringComparison.Ordinal);
        Assert.Contains("\"$PIPELINE_WORKSPACE/app\"/.", beanstalk, StringComparison.Ordinal);
        AssertBash(All.Select(platform => CloudDeploy.Deploy(spec, spec.Environments[0], platform, "abc")));

        spec.Deploy.CloudRegion = null;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("Write the AWS region", StringComparison.Ordinal));
    }

    [Fact]
    public void Google_cloud_targets_use_workload_identity_and_cloud_build()
    {
        var spec = GenerationTests.Spec(Target.GcpCloudRun, Stack.DotNet);
        var result = Generate(spec);
        var github = Content(result, GitHubGenerator.Path);
        Assert.Contains("uses: google-github-actions/auth@v3", github, StringComparison.Ordinal);
        Assert.Contains("gcloud builds submit . --tag \"us-central1-docker.pkg.dev/$GCP_PROJECT_ID/apps/my-app:${{ github.sha }}\" --quiet", github, StringComparison.Ordinal);
        Assert.Contains("gcloud run deploy app-my-app-prod --image", github, StringComparison.Ordinal);

        var azure = Content(result, AzureDevOpsGenerator.Path);
        Assert.Contains("gcloud auth activate-service-account", azure, StringComparison.Ordinal);
        Assert.Contains("GCP_CREDENTIALS: $(GCP_CREDENTIALS_PROD)", azure, StringComparison.Ordinal);

        var gitlab = Map(YamlWriterTests.Parse(Content(result, GitLabGenerator.Path)), "deploy-prod");
        Assert.Equal("google/cloud-sdk:slim", Text(gitlab, "image"));
        Assert.Equal("https://iam.googleapis.com/$GCP_WORKLOAD_IDENTITY_PROVIDER", Text(Map(Map(gitlab, "id_tokens"), "GCP_JWT"), "aud"));

        var functions = Content(Generate(GenerationTests.Spec(Target.GcpCloudFunctions, Stack.Node)), GitHubGenerator.Path);
        Assert.Contains("gcloud functions deploy app-my-app-prod --gen2 --region us-central1 --runtime nodejs22 --entry-point handler --source web --trigger-http --quiet", functions, StringComparison.Ordinal);
        Assert.Contains("gcloud app deploy app.yaml --quiet", Content(Generate(GenerationTests.Spec(Target.GcpAppEngine, Stack.Python)), GitLabGenerator.Path), StringComparison.Ordinal);
        var gke = Content(Generate(GenerationTests.Spec(Target.GcpGke, Stack.DotNet)), GitHubGenerator.Path);
        Assert.Contains("install_components: gke-gcloud-auth-plugin", gke, StringComparison.Ordinal);
        Assert.Contains("kubectl rollout status -n default deployment/my-app --timeout=300s", gke, StringComparison.Ordinal);
        Assert.Contains("## 1. Google Cloud identity and permissions", Content(result, SetupGuide.Path), StringComparison.Ordinal);
    }

    /// <summary>bash -n finds syntax errors; Git for Windows brings bash (checks nothing when there is none).</summary>
    internal static void AssertBash(IEnumerable<string> scripts)
    {
        var bash = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") : "/bin/bash";
        if (!File.Exists(bash))
        {
            return;
        }

        var folder = Directory.CreateTempSubdirectory("cicdpipelinegenerator-");
        try
        {
            var index = 0;
            foreach (var script in scripts)
            {
                var path = Path.Combine(folder.FullName, $"script{index++}.sh");
                File.WriteAllText(path, script);
                var (code, output) = ServerDeployTests.Run(bash, ["-n", path], null);
                Assert.True(code == 0, $"{output}\n{script}");
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static PipelineSpec Linux()
    {
        var spec = Example("windows-service");
        spec.Deploy.Target = Target.LinuxService;
        foreach (var environment in spec.Environments)
        {
            environment.ResourceName = "billing";
            environment.TargetPath = "/opt/billing";
        }

        return spec;
    }

    [Fact]
    public void A_linux_service_is_deployed_with_systemctl_and_rsync_on_its_own_runners()
    {
        var spec = Linux();
        spec.Deploy.RollbackPipeline = true;
        spec.Environments[2].Settings = [new() { Name = "Logging:LogLevel:Default", Value = "Warning" }];
        var result = Generate(spec);

        var prod = Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "deploy-prod");
        Assert.Equal(["self-hosted", "linux", "app-prod"], Seq(prod, "runs-on"));
        var deploy = Steps(prod).Last();
        Assert.Null(Text(deploy, "shell"));
        var script = Text(deploy, "run")!;
        Assert.Contains("name='billing'", script, StringComparison.Ordinal);
        Assert.Contains("target='/opt/billing'", script, StringComparison.Ordinal);
        Assert.Contains("keep=(--exclude 'appsettings.Production.json' --exclude 'logs')", script, StringComparison.Ordinal);
        Assert.Contains("sudo -n systemctl stop \"$name\"", script, StringComparison.Ordinal);
        Assert.Contains("copy_folder \"$backup\" \"$target\"", script, StringComparison.Ordinal);
        Assert.Contains("python3 - \"out/app/appsettings.json\" <<'PY'", Text(Steps(prod)[^2], "run"), StringComparison.Ordinal);

        var azure = Content(result, AzureDevOpsGenerator.Path);
        Assert.Contains("resourceType: VirtualMachine", azure, StringComparison.Ordinal);
        Assert.Contains("source_dir=\"$PIPELINE_WORKSPACE/app\"", azure, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell:", azure, StringComparison.Ordinal);
        Assert.Equal(["app-prod"], Seq(Map(YamlWriterTests.Parse(Content(result, GitLabGenerator.Path)), "deploy-prod"), "tags"));
        Assert.Contains("copy_folder \"$backup\" \"$target\"", Content(result, ".github/workflows/rollback.yml"), StringComparison.Ordinal);

        var scripts = All.SelectMany(platform => new[] { ServerDeploy.Script(spec, spec.Environments[2], platform), ServerDeploy.Script(spec, spec.Environments[2], platform, rollback: true), LinuxDeploy.SettingsScript(spec, spec.Environments[2], platform) }).ToList();
        Assert.All(scripts, item => Assert.DoesNotContain("$(", item, StringComparison.Ordinal));
        AssertBash(scripts);

        spec.Environments[0].TargetPath = "opt/billing";
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("full path such as /opt/billing", StringComparison.Ordinal));
    }

    [Fact]
    public void Servers_without_a_runner_are_reached_over_ssh_or_winrm_from_a_hosted_agent()
    {
        var linux = Linux();
        linux.Deploy.Remote = true;
        foreach (var environment in linux.Environments)
        {
            environment.ServerLabel = null;
            environment.Hosts = [$"app1-{environment.Name}.contoso.com", $"app2-{environment.Name}.contoso.com"];
        }

        var result = Generate(linux);
        var prod = Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "deploy-prod");
        Assert.Equal("ubuntu-latest", Text(prod, "runs-on"));
        var step = Steps(prod).Last();
        Assert.Equal("${{ secrets.DEPLOY_SSH_KEY }}", Text(Map(step, "env"), "DEPLOY_SSH_KEY"));
        var script = Text(step, "run")!;
        Assert.Contains("-o StrictHostKeyChecking=yes", script, StringComparison.Ordinal);
        Assert.Contains("for server in app1-prod.contoso.com app2-prod.contoso.com; do", script, StringComparison.Ordinal);
        Assert.Contains("rsync -az --delete -e \"ssh $ssh_options\" \"$PWD/out/app\"/ \"$DEPLOY_SSH_USER@$server:.cicd-billing-worker/\"", script, StringComparison.Ordinal);
        Assert.Contains("source_dir=\"$HOME/.cicd-billing-worker\"", script, StringComparison.Ordinal);
        AssertBash([script]);
        Assert.DoesNotContain("resourceType: VirtualMachine", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        Assert.Equal("ubuntu:24.04", Text(Map(YamlWriterTests.Parse(Content(result, GitLabGenerator.Path)), "deploy-prod"), "image"));
        Assert.Contains("`DEPLOY_SSH_KNOWN_HOSTS`", Content(result, SetupGuide.Path), StringComparison.Ordinal);

        var windows = Example("iis-site");
        windows.Deploy.Remote = true;
        foreach (var environment in windows.Environments)
        {
            environment.ServerLabel = null;
            environment.Hosts = [$"web-{environment.Name}.contoso.com"];
        }

        result = Generate(windows);
        prod = Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "deploy-prod");
        Assert.Equal("windows-latest", Text(prod, "runs-on"));
        script = Text(Steps(prod).Last(), "run")!;
        Assert.Contains("New-PSSession -ComputerName $server -UseSSL -Credential $credential", script, StringComparison.Ordinal);
        Assert.Contains("$source = Join-Path $env:TEMP 'cicd-portal'", script, StringComparison.Ordinal);
        Assert.Contains("foreach ($server in @('web-prod.contoso.com')) {", script, StringComparison.Ordinal);
        ServerDeployTests.AssertParses(All.Select(platform => ServerDeploy.Script(windows, windows.Environments[2], platform)));
        Assert.Contains("vmImage: windows-latest", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        Assert.Equal(["saas-windows-medium-amd64"], Seq(Map(YamlWriterTests.Parse(Content(result, GitLabGenerator.Path)), "deploy-prod"), "tags"));

        windows.Environments[0].Hosts = [];
        Assert.Contains(PipelineRules.Check(windows), message => message.Text.Contains("write the addresses of its servers", StringComparison.Ordinal));
    }

    [Fact]
    public void Coverage_and_sonarqube_wrap_the_build()
    {
        var spec = Example("dotnet-app-service");
        spec.Build.Coverage = true;
        spec.Build.SonarProjectKey = "contoso_my-app";
        var result = Generate(spec);
        var steps = Steps(Map(Map(YamlWriterTests.Parse(Content(result, GitHubGenerator.Path)), "jobs"), "build")).Select(step => Text(step, "name")).Where(name => name is not null).ToList();
        Assert.True(steps.IndexOf("Start the SonarQube analysis") < steps.IndexOf("Restore"));
        Assert.True(steps.IndexOf("Finish the SonarQube analysis") > steps.IndexOf("Test"));
        var github = Content(result, GitHubGenerator.Path);
        Assert.Contains("dotnet sonarscanner begin /k:\"contoso_my-app\" /d:sonar.host.url=\"$SONAR_HOST_URL\" /d:sonar.token=\"$SONAR_TOKEN\"", github, StringComparison.Ordinal);
        Assert.Contains("SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}", github, StringComparison.Ordinal);
        Assert.Contains("--collect:\"XPlat Code Coverage\" --results-directory out/test-results", github, StringComparison.Ordinal);
        Assert.Contains("task: PublishCodeCoverageResults@1", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("coverage_format: cobertura", Content(result, GitLabGenerator.Path), StringComparison.Ordinal);

        var java = Example("aks");
        java.Build.SonarProjectKey = "catalog";
        Assert.Contains("mvn -B sonar:sonar -Dsonar.projectKey=catalog", Content(Generate(java), GitLabGenerator.Path), StringComparison.Ordinal);

        var node = Example("node-static-web-app");
        node.Build.SonarProjectKey = "site";
        Assert.Contains(PipelineRules.Check(node), message => message.Text.Contains("SonarQube analyses .NET and Java", StringComparison.Ordinal));
    }

    private static List<YamlMappingNode> Steps(YamlMappingNode job) =>
        ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToList();

    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node.Children[new YamlScalarNode(key)];

    private static string? Text(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? ((YamlScalarNode)value).Value : null;

    private static List<string> Seq(YamlMappingNode node, string key) =>
        ((YamlSequenceNode)node.Children[new YamlScalarNode(key)]).Children.Select(item => ((YamlScalarNode)item).Value!).ToList();
}
