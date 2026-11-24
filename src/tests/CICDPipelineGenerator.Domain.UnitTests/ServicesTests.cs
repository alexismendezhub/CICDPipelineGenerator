using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

/// <summary>Services for the tests, and the tests with other versions of the language.</summary>
public class ServicesTests
{
    private static PipelineSpec Spec(Stack stack = Stack.Node)
    {
        var spec = GenerationTests.Spec(Target.AppService, stack);
        spec.Platforms = [.. GenerationTests.Six];
        return spec;
    }

    private static GenerationResult Generate(PipelineSpec spec)
    {
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        GenerationTests.AssertActionlint(result.Files.Single(file => file.Path.StartsWith(".github/", StringComparison.Ordinal)).Content);
        return result;
    }

    private static string Content(GenerationResult result, string path) => result.Files.Single(file => file.Path == path).Content;

    public static TheoryData<ServiceKind> Kinds() => [.. Enum.GetValues<ServiceKind>()];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_service_is_started_next_to_the_build_in_every_system(ServiceKind kind)
    {
        var spec = Spec();
        spec.Build.Services = [kind];
        var service = Services.Of(kind);
        var host = service.Name.ToUpperInvariant() + "_HOST";
        var result = Generate(spec);

        var github = YamlWriterTests.Parse(Content(result, GitHubGenerator.PathFor(spec)));
        var build = (YamlMappingNode)((YamlMappingNode)github["jobs"])["build"];
        Assert.Equal(service.Image, ((YamlMappingNode)((YamlMappingNode)build["services"])[service.Name])["image"].ToString());
        Assert.Equal("localhost", ((YamlMappingNode)build["env"])[host].ToString());
        Assert.False(((YamlMappingNode)((YamlMappingNode)github["jobs"])["deploy-" + spec.Environments[0].Name]).Children.ContainsKey("services"));

        var azure = Content(result, AzureDevOpsGenerator.PathFor(spec));
        Assert.Contains($"- container: {service.Name}", azure, StringComparison.Ordinal);
        Assert.Contains($"{service.Name}: {service.Name}", azure, StringComparison.Ordinal);
        Assert.Contains($"{host}: localhost", azure, StringComparison.Ordinal);

        // GitLab gives the service a name on the job's network.
        var gitlab = Content(result, GitLabGenerator.Path);
        Assert.Contains($"alias: {service.Name}", gitlab, StringComparison.Ordinal);
        Assert.Contains($"{host}: {service.Name}", gitlab, StringComparison.Ordinal);
        Assert.Contains($"/dev/tcp/{service.Name}/{service.Port}", gitlab, StringComparison.Ordinal);

        var jenkins = Content(result, JenkinsGenerator.Path);
        Assert.Contains($"docker run -d --name {spec.Name}-{service.Name} -p {service.Port}:{service.Port}", jenkins, StringComparison.Ordinal);
        Assert.Contains("Stop the services", jenkins, StringComparison.Ordinal);

        var bitbucket = YamlWriterTests.Parse(Content(result, BitbucketGenerator.Path));
        Assert.Equal(service.Image, ((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)bitbucket["definitions"])["services"])[service.Name])["image"].ToString());

        var circle = YamlWriterTests.Parse(Content(result, CircleCiGenerator.Path));
        var images = (YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)circle["jobs"])["build"])["docker"];
        Assert.Equal(service.Image, ((YamlMappingNode)images.Children[1])["image"].ToString());

        foreach (var file in result.Files.Where(file => file.Path != SetupGuide.Path))
        {
            Assert.Contains($"/{service.Port}) 2>/dev/null && exit 0", file.Content, StringComparison.Ordinal);
        }

        foreach (var platform in new[] { Platform.Jenkins, Platform.Bitbucket, Platform.CircleCi })
        {
            CoverageTests.AssertBash(ShellPlan.Create(spec, platform).Jobs[0].Steps.Select(step => Systems.WithEnv(step.Script, step.Env, powerShell: false)));
        }
    }

    [Fact]
    public void Services_need_a_linux_build()
    {
        var spec = Spec(Stack.DotNet);
        spec.Build.Services = [ServiceKind.SqlServer];
        spec.Build.Agent = BuildAgent.HostedWindows;
        spec.Platforms = [Platform.GitHubActions];

        Assert.Contains(PipelineGenerator.Generate(spec).Messages, message => message.Severity == Severity.Error && message.Text.Contains("Linux containers", StringComparison.Ordinal));
    }

    [Fact]
    public void The_tests_also_run_with_the_other_versions_and_the_deploy_waits_for_them()
    {
        var spec = Spec();
        spec.Build.Version = "22";
        spec.Build.AlsoTestWith = ["20", "24"];
        spec.Build.Services = [ServiceKind.Redis];
        var result = Generate(spec);
        var first = spec.Environments[0];

        var github = (YamlMappingNode)YamlWriterTests.Parse(Content(result, GitHubGenerator.PathFor(spec)))["jobs"];
        Assert.Equal(["build", "test-20", "test-24"], github.Children.Keys.Select(key => key.ToString()).Take(3));
        var other = (YamlMappingNode)github["test-20"];
        Assert.Equal("Test with 20", other["name"].ToString());
        Assert.Matches("node-version: .?20", Content(result, GitHubGenerator.PathFor(spec)));
        Assert.True(other.Children.ContainsKey("services"));
        Assert.DoesNotContain(((YamlSequenceNode)other["steps"]).Children.OfType<YamlMappingNode>(), step => step.Children.ContainsKey("uses") && step["uses"].ToString().StartsWith("actions/upload-artifact", StringComparison.Ordinal));
        Assert.Equal(["build", "test-20", "test-24"], ((YamlSequenceNode)((YamlMappingNode)github["deploy-" + first.Name])["needs"]).Children.Select(node => node.ToString()));

        var azure = YamlWriterTests.Parse(Content(result, AzureDevOpsGenerator.PathFor(spec)));
        var stage = (YamlMappingNode)((YamlSequenceNode)azure["stages"]).Children[0];
        Assert.Equal(["Build", "test_20", "test_24"], ((YamlSequenceNode)stage["jobs"]).Children.Select(job => ((YamlMappingNode)job)["job"].ToString()));

        var gitlab = YamlWriterTests.Parse(Content(result, GitLabGenerator.Path));
        var tested = (YamlMappingNode)gitlab["test-24"];
        Assert.Equal("build", tested["stage"].ToString());
        Assert.Contains("24", tested["image"].ToString(), StringComparison.Ordinal);
        Assert.False(tested.Children.ContainsKey("artifacts"));

        foreach (var platform in new[] { Platform.Jenkins, Platform.Bitbucket, Platform.CircleCi })
        {
            var plan = ShellPlan.Create(spec, platform);
            Assert.Equal(["build", "test-20", "test-24"], plan.Jobs.Take(3).Select(job => job.Id));
            Assert.Empty(plan.Jobs[1].Keeps);
            Assert.Empty(plan.Jobs[1].Results);
        }

        Assert.Contains("stage('Test with 20')", Content(result, JenkinsGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("name: Test with 24", Content(result, BitbucketGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("test-20:", Content(result, CircleCiGenerator.Path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("latest", "versions like 20 or 3.12")]
    [InlineData("22", "none equal to the version")]
    public void A_version_that_is_not_one_is_refused(string version, string text)
    {
        var spec = Spec();
        spec.Build.Version = "22";
        spec.Build.AlsoTestWith = [version];

        Assert.Contains(PipelineGenerator.Generate(spec).Messages, message => message.Severity == Severity.Error && message.Text.Contains(text, StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_changes_without_services_or_versions()
    {
        var result = Generate(Spec());

        Assert.DoesNotContain(result.Files, file => file.Path != SetupGuide.Path && (file.Content.Contains("/dev/tcp/", StringComparison.Ordinal) || file.Content.Contains("Test with", StringComparison.Ordinal)));
    }
}

/// <summary>A copy of the application per pull request.</summary>
public class PreviewTests
{
    [Fact]
    public void A_pull_request_gets_a_slot_and_loses_it_when_it_closes()
    {
        var spec = GenerationTests.Spec(Target.AppService);
        spec.Platforms = [Platform.GitHubActions];
        spec.Deploy.PreviewPullRequests = true;
        var result = PipelineGenerator.Generate(spec);
        var first = spec.Environments[0];

        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        Assert.DoesNotContain(result.Messages, message => message.Text.Contains("preview", StringComparison.OrdinalIgnoreCase));
        var workflow = result.Files.Single(file => file.Path == GitHubGenerator.PathFor(spec)).Content;
        var cleanup = result.Files.Single(file => file.Path == GitHubGenerator.PreviewCleanupPathFor(spec)).Content;
        GenerationTests.AssertActionlint(workflow);
        GenerationTests.AssertActionlint(cleanup);

        var job = (YamlMappingNode)((YamlMappingNode)YamlWriterTests.Parse(workflow)["jobs"])["preview"];
        Assert.Equal("build", job["needs"].ToString());
        Assert.Contains("github.event.pull_request.head.repo.full_name == github.repository", job["if"].ToString(), StringComparison.Ordinal);
        Assert.Equal(first.Name, job["environment"].ToString());
        Assert.Equal("write", ((YamlMappingNode)job["permissions"])["pull-requests"].ToString());
        Assert.Equal("pr-${{ github.event.number }}", ((YamlMappingNode)job["env"])["SLOT"].ToString());
        Assert.Contains("az webapp deployment slot create", workflow, StringComparison.Ordinal);
        Assert.Contains("gh pr comment", workflow, StringComparison.Ordinal);

        Assert.Contains("types:\n      - closed", cleanup, StringComparison.Ordinal);
        Assert.Contains("az webapp deployment slot delete", cleanup, StringComparison.Ordinal);
        Assert.Contains("pr-<number>", result.Files.Single(file => file.Path == SetupGuide.Path).Content, StringComparison.Ordinal);
        CoverageTests.AssertBash(((YamlSequenceNode)job["steps"]).Children.OfType<YamlMappingNode>().Where(step => step.Children.ContainsKey("run")).Select(step => step["run"].ToString()));
    }

    [Fact]
    public void Where_no_preview_is_written_it_is_said()
    {
        var other = GenerationTests.Spec(Target.ContainerApp);
        other.Deploy.PreviewPullRequests = true;
        var none = PipelineGenerator.Generate(other);
        Assert.Contains(none.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("needs blue-green or canary", StringComparison.Ordinal));
        Assert.DoesNotContain(none.Files, file => file.Path.Contains("preview", StringComparison.Ordinal));

        var spec = GenerationTests.Spec(Target.AppService);
        spec.Deploy.PreviewPullRequests = true;
        var result = PipelineGenerator.Generate(spec);
        Assert.Contains(result.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("GitHub Actions and GitLab", StringComparison.Ordinal));
        Assert.DoesNotContain("pr-", result.Files.Single(file => file.Path == AzureDevOpsGenerator.Path).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void On_gitlab_a_merge_request_gets_a_review_app()
    {
        var spec = GenerationTests.Spec(Target.AppService);
        spec.Platforms = [Platform.GitLab];
        spec.Deploy.PreviewPullRequests = true;
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        Assert.DoesNotContain(result.Messages, message => message.Text.Contains("preview", StringComparison.OrdinalIgnoreCase));
        SchemaTests.AssertValid(result);

        var root = YamlWriterTests.Parse(result.Files.Single(file => file.Path == GitLabGenerator.Path).Content);
        var preview = (YamlMappingNode)root["preview"];
        var environment = (YamlMappingNode)preview["environment"];
        Assert.Equal("review/$CI_MERGE_REQUEST_IID", environment["name"].ToString());
        Assert.Equal("stop-preview", environment["on_stop"].ToString());
        Assert.Equal("$PREVIEW_URL", environment["url"].ToString());
        var stop = (YamlMappingNode)root["stop-preview"];
        Assert.Equal("stop", ((YamlMappingNode)stop["environment"])["action"].ToString());
        Assert.Contains("slot delete", ((YamlSequenceNode)stop["script"]).Children[0].ToString(), StringComparison.Ordinal);
        CoverageTests.AssertBash(((YamlSequenceNode)preview["script"]).Children.Select(line => line.ToString()));
        Assert.Contains("review/*", result.Files.Single(file => file.Path == SetupGuide.Path).Content, StringComparison.Ordinal);
    }
}

/// <summary>Container Apps: the update waits for the new revision, and a pull request gets a revision of its own.</summary>
public class ContainerAppTests
{
    [Fact]
    public void The_apps_own_update_waits_until_the_new_revision_is_healthy()
    {
        var spec = GenerationTests.Spec(Target.ContainerApp);
        var script = Commands.ContainerAppUpdate(spec, spec.Environments[^1], "registry.azurecr.io/app:abc");
        Assert.Contains("--query properties.latestRevisionName", script, StringComparison.Ordinal);
        Assert.Contains("if [ \"$health\" != Healthy ]; then echo \"The new revision $new is not healthy.\"; exit 1; fi", script, StringComparison.Ordinal);
        CoverageTests.AssertBash([script]);

        // With blue-green, the revision that has the traffic is read from the traffic, not taken as the newest one.
        spec.Deploy.Strategy = DeployStrategy.BlueGreen;
        var swap = Commands.ContainerAppUpdate(spec, spec.Environments[^1], "registry.azurecr.io/app:abc");
        Assert.Contains("az containerapp ingress traffic show", swap, StringComparison.Ordinal);
        Assert.Contains("sort_by([?revisionName], &weight)[-1].revisionName", swap, StringComparison.Ordinal);
        CoverageTests.AssertBash([swap]);
    }

    [Fact]
    public void A_pull_request_gets_a_revision_without_traffic_and_loses_it_when_it_closes()
    {
        var spec = GenerationTests.Spec(Target.ContainerApp);
        spec.Platforms = [Platform.GitHubActions];
        spec.Deploy.Strategy = DeployStrategy.BlueGreen;
        spec.Deploy.PreviewPullRequests = true;
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        Assert.DoesNotContain(result.Messages, message => message.Text.Contains("preview", StringComparison.OrdinalIgnoreCase));
        var workflow = result.Files.Single(file => file.Path == GitHubGenerator.PathFor(spec)).Content;
        var cleanup = result.Files.Single(file => file.Path == GitHubGenerator.PreviewCleanupPathFor(spec)).Content;
        GenerationTests.AssertActionlint(workflow);
        GenerationTests.AssertActionlint(cleanup);

        var job = (YamlMappingNode)((YamlMappingNode)YamlWriterTests.Parse(workflow)["jobs"])["preview"];
        Assert.Equal("pr-${{ github.event.number }}-${{ github.run_number }}", ((YamlMappingNode)job["env"])["SUFFIX"].ToString());
        var scripts = ((YamlSequenceNode)job["steps"]).Children.OfType<YamlMappingNode>().Where(step => step.Children.ContainsKey("run")).Select(step => step["run"].ToString()).ToList();
        CoverageTests.AssertBash(scripts);
        var all = string.Join("\n", scripts);
        Assert.Contains(":$SUFFIX --file", all, StringComparison.Ordinal);
        Assert.Contains("az containerapp revision copy", all, StringComparison.Ordinal);
        Assert.Contains("--revision-suffix \"$SUFFIX\"", all, StringComparison.Ordinal);

        // It refuses to start while a new revision would take the traffic of production.
        Assert.Contains("if [ \"$mode\" != Multiple ]", all, StringComparison.Ordinal);
        Assert.Contains("contains(name, '--pr-$NUMBER-')", all, StringComparison.Ordinal);
        Assert.Contains("az containerapp revision deactivate", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("slot", cleanup, StringComparison.Ordinal);
        Assert.Contains("ties the traffic to a revision by name", result.Files.Single(file => file.Path == SetupGuide.Path).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_apps_own_update_no_preview_is_written_and_it_is_said()
    {
        var spec = GenerationTests.Spec(Target.ContainerApp);
        spec.Platforms = [Platform.GitHubActions];
        spec.Deploy.PreviewPullRequests = true;
        var result = PipelineGenerator.Generate(spec);
        Assert.Contains(result.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("needs blue-green or canary", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Files, file => file.Path.Contains("preview", StringComparison.Ordinal));
    }

    [Fact]
    public void The_setup_lets_the_identity_join_the_app_to_its_environment()
    {
        var spec = GenerationTests.Spec(Target.ContainerApp);
        spec.Platforms = [Platform.GitHubActions];
        spec.SetupScript = true;
        spec.Repository ??= "contoso/my-app";
        var result = PipelineGenerator.Generate(spec);
        var script = result.Files.Single(file => file.Path == SetupScript.Path).Content;
        Assert.Contains("--query properties.managedEnvironmentId", script, StringComparison.Ordinal);
        Assert.Contains("--role \"Contributor\" --scope \"$managed\"", script, StringComparison.Ordinal);
        Assert.Contains("managedEnvironments/join/action", result.Files.Single(file => file.Path == SetupGuide.Path).Content, StringComparison.Ordinal);
    }
}
