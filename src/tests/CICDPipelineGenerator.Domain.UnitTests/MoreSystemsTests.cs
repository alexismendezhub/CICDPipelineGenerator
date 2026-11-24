using System.Text.RegularExpressions;
using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

/// <summary>Travis CI, Google Cloud Build, AWS CodeBuild, TeamCity and Argo Workflows: every job as one script in a Linux image.</summary>
public partial class MoreSystemsTests
{
    private static readonly Platform[] More = [Platform.TravisCi, Platform.CloudBuild, Platform.CodePipeline, Platform.TeamCity, Platform.ArgoWorkflows];

    private static string Content(GenerationResult result, string path) => result.Files.Single(file => file.Path == path).Content;

    private static PipelineSpec Spec(Target target, Stack stack = Stack.DotNet, Flow flow = Flow.Promotion)
    {
        var spec = GenerationTests.Spec(target, stack, flow);
        spec.Platforms = [.. More];
        spec.PathFilters = [];
        spec.Schedule = null;
        spec.NotifyOnFailure = false;
        spec.NotifyOnSuccess = false;
        spec.Deploy.RollbackPipeline = false;
        return spec;
    }

    private static bool LinuxOnly(PipelineSpec spec) => spec.IsWindowsBuild || spec.IsMacBuild || spec.DeploysOnMac || spec.OnServerRunner || (spec.IsRemote && spec.UsesPowerShell);

    [Theory]
    [MemberData(nameof(GenerationTests.Combinations), MemberType = typeof(GenerationTests))]
    public void Every_combination_is_written_for_the_five_systems_or_refused_with_the_reason(Target target, Stack stack, Flow flow)
    {
        var spec = Spec(target, stack, flow);
        var result = PipelineGenerator.Generate(spec);
        if (LinuxOnly(spec))
        {
            Assert.Contains(result.Messages, message => message.Severity == Severity.Error && message.Text.Contains("Linux images", StringComparison.Ordinal));
            return;
        }

        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        var plan = ShellPlan.Create(spec, Platform.CodePipeline);
        Assert.Equal(
            [MoreSystems.TravisPath, MoreSystems.CloudBuildPath, .. plan.Jobs.Select(MoreSystems.BuildspecPath), MoreSystems.TeamCityPath, MoreSystems.ArgoPath, SetupGuide.Path],
            result.Files.Select(file => file.Path));

        var scripts = new List<string>();
        foreach (var file in result.Files.Where(file => file.Path.EndsWith(".yml", StringComparison.Ordinal) || file.Path.EndsWith(".yaml", StringComparison.Ordinal)))
        {
            var root = YamlWriterTests.Parse(file.Content);
            Assert.DoesNotContain("\r", file.Content);
            Assert.DoesNotContain("${{", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("$CI_", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("$CIRCLE_", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("$BITBUCKET_", file.Content, StringComparison.Ordinal);
            scripts.AddRange(Scalars(root).Where(text => text.Contains("set -e\n", StringComparison.Ordinal)).Select(text => Argo().Replace(text, "x")));
        }

        // One script per job in each of the four YAML systems, and the checkout of Argo Workflows.
        Assert.Equal(new[] { Platform.TravisCi, Platform.CloudBuild, Platform.CodePipeline, Platform.ArgoWorkflows }.Sum(platform => ShellPlan.Create(spec, platform).Jobs.Count) + 1, scripts.Count);
        CoverageTests.AssertBash(scripts.Where(script => !script.StartsWith("#!/usr/bin/env sh", StringComparison.Ordinal)));

        var argo = Content(result, MoreSystems.ArgoPath);
        Assert.All(Template().Matches(argo), match => Assert.StartsWith("{{workflow.parameters.", match.Value, StringComparison.Ordinal));

        var kotlin = Content(result, MoreSystems.TeamCityPath);
        Assert.Equal(plan.Jobs.Count, Regex.Count(kotlin, @"^object \w+ : BuildType\(\{$", RegexOptions.Multiline));
        Assert.Equal(plan.Jobs.Count, Regex.Count(kotlin, @"^    buildType\(\w+\)$", RegexOptions.Multiline));
        Assert.Equal(Regex.Count(kotlin, @"\{"), Regex.Count(kotlin, @"\}"));
        Assert.Equal(0, Regex.Count(kotlin, "\"\"\"") % 2);
        foreach (Match body in RawString().Matches(kotlin))
        {
            // Inside a raw string every dollar is written ${'$'} and every percent is doubled.
            var text = body.Groups[1].Value.Replace("${'$'}", "", StringComparison.Ordinal).Replace("%%", "", StringComparison.Ordinal);
            Assert.DoesNotContain("$", text, StringComparison.Ordinal);
            Assert.DoesNotContain("%", text, StringComparison.Ordinal);
        }

        var setup = Content(result, SetupGuide.Path);
        foreach (var platform in More)
        {
            Assert.Contains($"## {Systems.Name(platform)}", setup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_app_service_is_built_once_and_deployed_per_environment_in_each_system()
    {
        var spec = Spec(Target.AppService);
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors);
        var last = spec.Environments[^1];

        var travis = YamlWriterTests.Parse(Content(result, MoreSystems.TravisPath));
        var jobs = (YamlSequenceNode)((YamlMappingNode)travis["jobs"])["include"];
        Assert.Equal(1 + spec.Environments.Count, jobs.Children.Count);
        Assert.Contains("docker run --rm", Content(result, MoreSystems.TravisPath), StringComparison.Ordinal);
        Assert.Contains($"-e AZURE_CLIENT_SECRET=\"${Secrets.AzureDevOpsName(last, "AZURE_CLIENT_SECRET")}\"", Content(result, MoreSystems.TravisPath), StringComparison.Ordinal);

        var cloud = Content(result, MoreSystems.CloudBuildPath);
        Assert.Contains("automapSubstitutions: true", cloud, StringComparison.Ordinal);
        Assert.Contains($"versionName: projects/$PROJECT_ID/secrets/{Secrets.AzureDevOpsName(last, "AZURE_CLIENT_SECRET")}/versions/latest", cloud, StringComparison.Ordinal);
        Assert.Contains($"if [ \"$BRANCH_NAME\" != \"{spec.BranchOf(last)}\" ]", cloud, StringComparison.Ordinal);

        var buildspec = Content(result, "buildspec.yml");
        Assert.Contains("version: 0.2", buildspec, StringComparison.Ordinal);
        Assert.Contains("artifacts:", buildspec, StringComparison.Ordinal);
        Assert.Contains($"AZURE_CLIENT_SECRET: /{spec.Name}/{last.Name}/AZURE_CLIENT_SECRET", string.Concat(result.Files.Where(file => file.Path.StartsWith("buildspec-", StringComparison.Ordinal)).Select(file => file.Content)), StringComparison.Ordinal);

        var kotlin = Content(result, MoreSystems.TeamCityPath);
        Assert.Contains("object Build : BuildType({", kotlin, StringComparison.Ordinal);
        Assert.Contains("type = BuildTypeSettings.Type.DEPLOYMENT", kotlin, StringComparison.Ordinal);
        Assert.Contains("snapshot(Build)", kotlin, StringComparison.Ordinal);
        Assert.Contains($"param(\"env.AZURE_CLIENT_SECRET\", \"%{Secrets.AzureDevOpsName(last, "AZURE_CLIENT_SECRET")}%\")", kotlin, StringComparison.Ordinal);

        var argo = Content(result, MoreSystems.ArgoPath);
        Assert.Contains("kind: WorkflowTemplate", argo, StringComparison.Ordinal);
        Assert.Contains($"name: {spec.Name}-{last.Name}", argo, StringComparison.Ordinal);
        Assert.Equal(spec.Environments.Count(environment => environment.RequireApproval), Regex.Count(argo, "template: approve"));
    }

    [Fact]
    public void Cloud_build_and_codebuild_use_their_own_identity_in_their_own_cloud()
    {
        var google = Spec(Target.GcpCloudRun);
        google.Platforms = [Platform.CloudBuild];
        var cloud = PipelineGenerator.Generate(google);
        Assert.False(cloud.HasErrors);
        Assert.DoesNotContain("GCP_CREDENTIALS", Content(cloud, MoreSystems.CloudBuildPath), StringComparison.Ordinal);
        Assert.Contains("export GCP_PROJECT_ID=\"$PROJECT_ID\"", Content(cloud, MoreSystems.CloudBuildPath), StringComparison.Ordinal);
        Assert.Contains("No key is stored", Content(cloud, SetupGuide.Path), StringComparison.Ordinal);

        var amazon = Spec(Target.AwsEcs);
        amazon.Platforms = [Platform.CodePipeline];
        var code = PipelineGenerator.Generate(amazon);
        Assert.False(code.HasErrors);
        Assert.DoesNotContain(code.Files, file => file.Path != SetupGuide.Path && file.Content.Contains("AWS_SECRET_ACCESS_KEY", StringComparison.Ordinal));
        Assert.Contains("No key is stored", Content(code, SetupGuide.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_files_do_not_hold_is_said()
    {
        var spec = Spec(Target.AppService);
        spec.Schedule = "0 6 * * 1-5";
        spec.Environments[^1].RequireApproval = true;
        var result = PipelineGenerator.Generate(spec);

        Assert.False(result.HasErrors);
        Assert.Contains(result.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("the schedule", StringComparison.Ordinal));
        Assert.Contains(result.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("no approval inside a build", StringComparison.Ordinal));

        // TeamCity: the deployment that needs approval is not started by the one before it.
        var kotlin = Content(result, MoreSystems.TeamCityPath);
        Assert.Equal(spec.Environments.Count(environment => !environment.RequireApproval), Regex.Count(kotlin, "finishBuildTrigger \\{"));
    }

    [Fact]
    public void Services_messages_and_test_results_are_written_for_the_five_systems()
    {
        var spec = Spec(Target.AppService, Stack.Java);
        spec.Build.Services = [ServiceKind.Postgres];
        spec.Build.PublishTestResults = true;
        spec.NotifyOnFailure = true;
        spec.NotifyOnSuccess = true;
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        Assert.DoesNotContain(result.Messages, message => message.Text.Contains("Services", StringComparison.Ordinal) && message.Text.Contains("not written", StringComparison.Ordinal));

        var travis = Content(result, MoreSystems.TravisPath);
        Assert.Contains("docker run -d --name " + spec.Name + "-postgres -p 5432:5432", travis, StringComparison.Ordinal);
        Assert.Contains("--network host", travis, StringComparison.Ordinal);
        Assert.Contains("-e NOTIFY_WEBHOOK", travis, StringComparison.Ordinal);

        var cloud = Content(result, MoreSystems.CloudBuildPath);
        Assert.Contains("id: service-postgres", cloud, StringComparison.Ordinal);
        Assert.Contains("/dev/tcp/postgres/5432", cloud, StringComparison.Ordinal);
        Assert.Contains("export POSTGRES_HOST='postgres'", cloud, StringComparison.Ordinal);

        var buildspec = Content(result, "buildspec.yml");
        Assert.Contains("file-format: JUNITXML", buildspec, StringComparison.Ordinal);
        Assert.Contains("docker run -d --name " + spec.Name + "-postgres", buildspec, StringComparison.Ordinal);
        Assert.Contains($"NOTIFY_WEBHOOK: /{spec.Name}/build/NOTIFY_WEBHOOK", Content(result, $"buildspec-deploy-{spec.Environments[^1].Name}.yml"), StringComparison.Ordinal);

        var kotlin = Content(result, MoreSystems.TeamCityPath);
        Assert.Contains("name = \"Start the services\"", kotlin, StringComparison.Ordinal);
        Assert.Contains("executionMode = BuildStep.ExecutionMode.ALWAYS", kotlin, StringComparison.Ordinal);
        Assert.Contains("dockerRunParameters = \"--network host\"", kotlin, StringComparison.Ordinal);
        Assert.Contains("param(\"env.NOTIFY_WEBHOOK\", \"%NOTIFY_WEBHOOK%\")", kotlin, StringComparison.Ordinal);

        var argo = YamlWriterTests.Parse(Content(result, MoreSystems.ArgoPath));
        var templates = (YamlSequenceNode)((YamlMappingNode)argo["spec"])["templates"];
        var build = templates.Children.OfType<YamlMappingNode>().Single(template => template["name"].ToString() == "build");
        Assert.Equal("postgres:17", ((YamlMappingNode)((YamlSequenceNode)build["sidecars"]).Children[0])["image"].ToString());

        foreach (var file in result.Files.Where(file => file.Path != SetupGuide.Path))
        {
            Assert.Contains("trap tell_failure EXIT", file.Content, StringComparison.Ordinal);
            if (!file.Path.StartsWith("buildspec", StringComparison.Ordinal) || file.Path == $"buildspec-deploy-{spec.Environments[^1].Name}.yml")
            {
                Assert.Contains($"{spec.Name} is deployed to {spec.Environments[^1].Name}", file.Content, StringComparison.Ordinal);
            }
        }

        foreach (var platform in More)
        {
            CoverageTests.AssertBash(ShellPlan.Create(spec, platform).Jobs.Select(job => MoreSystems.Script(spec, job, platform)));
        }
    }

    [Fact]
    public void Path_filters_a_schedule_and_the_cache_are_written_where_the_file_can_hold_them()
    {
        var spec = Spec(Target.AppService);
        spec.PathFilters = ["src/**"];
        spec.Schedule = "0 6 * * 1-5";
        spec.Build.CachePackages = true;
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));

        Assert.Contains("triggerRules = \"+:src/**\"", Content(result, MoreSystems.TeamCityPath), StringComparison.Ordinal);
        var cron = YamlWriterTests.Parse(Content(result, MoreSystems.ArgoCronPath));
        Assert.Equal("CronWorkflow", cron["kind"].ToString());
        Assert.Equal("0 6 * * 1-5", ((YamlMappingNode)cron["spec"])["schedule"].ToString());
        Assert.Equal("deploy-" + spec.Name, ((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)cron["spec"])["workflowSpec"])["workflowTemplateRef"])["name"].ToString());
        Assert.Contains("cache:\n  paths:", Content(result, "buildspec.yml"), StringComparison.Ordinal);
        Assert.Contains(result.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("set in the system", StringComparison.Ordinal));
        Assert.Contains(result.Messages, message => message.Severity == Severity.Warning && message.Text.StartsWith("TeamCity: add the schedule", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rollback_started_by_hand_is_written_where_the_system_has_one()
    {
        var spec = Spec(Target.LinuxService);
        spec.Deploy.Remote = true;
        spec.Deploy.RollbackPipeline = true;
        foreach (var environment in spec.Environments)
        {
            environment.ServerLabel = null;
            environment.Hosts = [$"{environment.Name}.contoso.com"];
        }

        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        var last = spec.Environments[^1].Name;

        Assert.Contains(result.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("Travis CI has no build started by hand", StringComparison.Ordinal));
        var cloud = Content(result, MoreSystems.CloudBuildRollbackPath);
        Assert.Contains($"if [ \"$_ENVIRONMENT\" != \"{last}\" ]", cloud, StringComparison.Ordinal);
        Assert.Contains("_ENVIRONMENT: " + spec.Environments[0].Name, cloud, StringComparison.Ordinal);
        Assert.Contains(result.Files, file => file.Path == $"buildspec-rollback-{last}.yml");
        var kotlin = Content(result, MoreSystems.TeamCityPath);
        Assert.Contains($"buildType(Rollback{char.ToUpperInvariant(last[0])}{last[1..]})", kotlin, StringComparison.Ordinal);
        var argo = Content(result, MoreSystems.ArgoRollbackPath);
        Assert.Contains("name: rollback-" + spec.Name, argo, StringComparison.Ordinal);
        Assert.Contains($"when: '{{{{workflow.parameters.environment}}}} == {last}'", argo.Replace("\"", "'", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("rollback", Content(result, SetupGuide.Path), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Scalars(YamlNode node) => node switch
    {
        YamlScalarNode scalar => [scalar.Value ?? ""],
        YamlMappingNode map => map.Children.SelectMany(pair => Scalars(pair.Value)),
        YamlSequenceNode list => list.Children.SelectMany(Scalars),
        _ => [],
    };

    [GeneratedRegex(@"\{\{[^}]*\}\}")]
    private static partial Regex Template();

    [GeneratedRegex(@"\{\{workflow\.[^}]*\}\}")]
    private static partial Regex Argo();

    [GeneratedRegex("\"\"\"(.*?)\"\"\"", RegexOptions.Singleline)]
    private static partial Regex RawString();
}
