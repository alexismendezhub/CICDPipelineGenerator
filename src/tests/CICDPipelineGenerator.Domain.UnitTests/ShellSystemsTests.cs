using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

/// <summary>Jenkins, Bitbucket Pipelines and CircleCI: the same jobs, written by three writers.</summary>
public class ShellSystemsTests
{
    private static readonly Platform[] Shell = [Platform.Jenkins, Platform.Bitbucket, Platform.CircleCi];

    private static string Content(GenerationResult result, string path) => result.Files.Single(file => file.Path == path).Content;

    private static PipelineSpec Spec(Target target, Stack stack = Stack.DotNet, Flow flow = Flow.Promotion)
    {
        var spec = GenerationTests.Spec(target, stack, flow);
        spec.Platforms = [.. Shell];

        // Bitbucket has no hosted Windows or macOS, so these builds use the person's own agents there.
        if (stack is Stack.Ios or Stack.Msix || target == Target.ServiceFabric)
        {
            spec.Build.Agent = stack == Stack.Ios ? BuildAgent.SelfHostedMac : BuildAgent.SelfHostedWindows;
            spec.Build.AgentLabel = "builders";
        }

        return spec;
    }

    private static GenerationResult Generate(PipelineSpec spec)
    {
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        foreach (var file in result.Files.Where(file => file.Path.StartsWith("Jenkinsfile", StringComparison.Ordinal)))
        {
            AssertGroovy(file.Content);
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(GenerationTests.Combinations), MemberType = typeof(GenerationTests))]
    public void Every_combination_is_written_for_the_three_shell_systems(Target target, Stack stack, Flow flow)
    {
        var result = Generate(Spec(target, stack, flow));

        Assert.Equal([JenkinsGenerator.Path, BitbucketGenerator.Path, CircleCiGenerator.Path, SetupGuide.Path], result.Files.Select(file => file.Path));
        foreach (var file in result.Files.Where(file => file.Path.EndsWith(".yml", StringComparison.Ordinal)))
        {
            Assert.True(YamlWriterTests.Parse(file.Content).Children.Count > 0, file.Path);
            Assert.DoesNotContain("\r", file.Content);
        }

        // Nothing of another system's own syntax leaks into the scripts.
        foreach (var file in result.Files.Where(file => file.Path != SetupGuide.Path))
        {
            Assert.DoesNotContain("${{", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("$CI_", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("PIPELINE_WORKSPACE", file.Content, StringComparison.Ordinal);
        }
    }

    public static TheoryData<Target> Targets()
    {
        var data = new TheoryData<Target>();
        foreach (var target in Enum.GetValues<Target>())
        {
            data.Add(target);
        }

        return data;
    }

    /// <summary>bash and Windows PowerShell read every script of every job without a syntax error.</summary>
    [Theory]
    [MemberData(nameof(Targets))]
    public void The_scripts_of_every_target_parse(Target target)
    {
        var stack = GenerationTests.Combinations().First(row => (Target)row[0] == target)[1];
        var spec = Spec(target, (Stack)stack);
        spec.Environments[^1].BeforeDeploy = ["echo before"];
        foreach (var platform in Shell)
        {
            var plan = ShellPlan.Create(spec, platform);
            var jobs = plan.Jobs.Concat(plan.Rollbacks).ToList();
            CoverageTests.AssertBash(jobs.Where(job => !job.Windows).Select(job => job.Preamble + "\n" + string.Join("\n", job.Steps.Select(step => Systems.WithEnv(step.Script, step.Env, powerShell: false)))));
            ServerDeployTests.AssertParses(jobs.Where(job => job.Windows).Select(job => string.Join("\n", job.Steps.Select(step => Systems.WithEnv(step.Script, step.Env, powerShell: true)))));
        }
    }

    [Fact]
    public void An_app_service_is_built_once_and_deployed_per_environment_in_each_system()
    {
        var spec = Spec(Target.AppService);
        spec.NotifyOnFailure = true;
        spec.Schedule = "0 6 * * 1-5";
        var result = Generate(spec);

        var jenkins = Content(result, JenkinsGenerator.Path);
        Assert.Contains("stage('Build and test') {", jenkins, StringComparison.Ordinal);
        Assert.Contains("stash name: 'build', includes: 'out/app/**'", jenkins, StringComparison.Ordinal);
        Assert.Contains("stage('Deploy to prod') {", jenkins, StringComparison.Ordinal);
        Assert.Contains("branch 'main'", jenkins, StringComparison.Ordinal);
        Assert.Contains("message 'Deploy to prod?'", jenkins, StringComparison.Ordinal);
        Assert.Contains("lock('my-app-prod')", jenkins, StringComparison.Ordinal);
        Assert.Contains("AZURE_CLIENT_SECRET = credentials('AZURE_CLIENT_SECRET_PROD')", jenkins, StringComparison.Ordinal);
        Assert.Contains("NOTIFY_WEBHOOK = credentials('NOTIFY_WEBHOOK')", jenkins, StringComparison.Ordinal);
        Assert.Contains("cron('0 6 * * 1-5')", jenkins, StringComparison.Ordinal);
        Assert.Contains("az webapp deploy --resource-group rg-contoso-prod --name app-my-app-prod --src-path app.zip --type zip --slot staging", jenkins, StringComparison.Ordinal);
        Assert.Contains("az login --service-principal --username \"$AZURE_CLIENT_ID\" --password \"$AZURE_CLIENT_SECRET\"", jenkins, StringComparison.Ordinal);

        var bitbucket = YamlWriterTests.Parse(Content(result, BitbucketGenerator.Path));
        var pipelines = (YamlMappingNode)bitbucket.Children[new YamlScalarNode("pipelines")];
        Assert.True(pipelines.Children.ContainsKey(new YamlScalarNode("pull-requests")));
        var main = (YamlSequenceNode)((YamlMappingNode)pipelines.Children[new YamlScalarNode("branches")]).Children[new YamlScalarNode("main")];
        Assert.Equal(4, main.Children.Count);
        var prod = (YamlMappingNode)((YamlMappingNode)main.Children[3]).Children[new YamlScalarNode("step")];
        Assert.Equal("prod", ((YamlScalarNode)prod.Children[new YamlScalarNode("deployment")]).Value);
        Assert.Equal("manual", ((YamlScalarNode)prod.Children[new YamlScalarNode("trigger")]).Value);
        Assert.Equal("mcr.microsoft.com/azure-cli:latest", ((YamlScalarNode)prod.Children[new YamlScalarNode("image")]).Value);
        Assert.Contains("BITBUCKET_EXIT_CODE", Content(result, BitbucketGenerator.Path), StringComparison.Ordinal);

        var circle = YamlWriterTests.Parse(Content(result, CircleCiGenerator.Path));
        var workflows = (YamlMappingNode)circle.Children[new YamlScalarNode("workflows")];
        var jobs = (YamlSequenceNode)((YamlMappingNode)workflows.Children[new YamlScalarNode("deploy")]).Children[new YamlScalarNode("jobs")];
        Assert.Equal(["build", "deploy-dev", "deploy-test", "approve-prod", "deploy-prod"], jobs.Children.Select(job => ((YamlScalarNode)((YamlMappingNode)job).Children.Keys.First()).Value));
        var circleText = Content(result, CircleCiGenerator.Path);
        Assert.Contains("- my-app-prod", circleText, StringComparison.Ordinal);
        Assert.Contains("type: approval", circleText, StringComparison.Ordinal);
        Assert.Contains("persist_to_workspace:", circleText, StringComparison.Ordinal);
        Assert.Contains("when: on_fail", circleText, StringComparison.Ordinal);
        Assert.True(workflows.Children.ContainsKey(new YamlScalarNode("scheduled")));

        var setup = Content(result, SetupGuide.Path);
        Assert.Contains("## Jenkins", setup, StringComparison.Ordinal);
        Assert.Contains("`AZURE_CLIENT_SECRET_PROD`", setup, StringComparison.Ordinal);
        Assert.Contains("## Bitbucket Pipelines", setup, StringComparison.Ordinal);
        Assert.Contains("## CircleCI", setup, StringComparison.Ordinal);
        Assert.Contains("`my-app-prod`", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void Aws_signs_in_with_the_token_where_the_system_has_one_and_with_keys_on_jenkins()
    {
        var spec = Spec(Target.AwsEcs);
        var result = Generate(spec);
        var bitbucket = Content(result, BitbucketGenerator.Path);
        Assert.Contains("oidc: true", bitbucket, StringComparison.Ordinal);
        Assert.Contains("printf '%s' \"$BITBUCKET_STEP_OIDC_TOKEN\" > \"$HOME/.aws-web-identity\"", bitbucket, StringComparison.Ordinal);
        Assert.Contains("- docker", bitbucket, StringComparison.Ordinal);
        Assert.Contains("docker push \"$registry/my-app:$BITBUCKET_COMMIT\"", bitbucket, StringComparison.Ordinal);

        var circle = Content(result, CircleCiGenerator.Path);
        Assert.Contains("$CIRCLE_OIDC_TOKEN_V2", circle, StringComparison.Ordinal);
        Assert.Contains("- setup_remote_docker", circle, StringComparison.Ordinal);
        Assert.Contains("image: cimg/python:3.12", circle, StringComparison.Ordinal);
        Assert.Contains("- my-app-build", circle, StringComparison.Ordinal);

        var jenkins = Content(result, JenkinsGenerator.Path);
        Assert.Contains("AWS_ACCESS_KEY_ID = credentials('AWS_ACCESS_KEY_ID_PROD')", jenkins, StringComparison.Ordinal);
        Assert.DoesNotContain("AWS_WEB_IDENTITY_TOKEN_FILE", jenkins, StringComparison.Ordinal);
        Assert.Contains("docker push \"$registry/my-app:$GIT_COMMIT\"", jenkins, StringComparison.Ordinal);
        Assert.Contains("https://oidc.circleci.com/org/<organization-id>", Content(result, SetupGuide.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_windows_service_runs_on_the_servers_own_runners_in_powershell()
    {
        var spec = Spec(Target.WindowsService);
        spec.Deploy.RollbackPipeline = true;
        spec.Environments[^1].Servers = ["web1", "web2"];
        var result = Generate(spec);

        var jenkins = Content(result, JenkinsGenerator.Path);
        Assert.Contains("agent { label 'web1' }", jenkins, StringComparison.Ordinal);
        Assert.Contains("stage('Deploy to prod (web2)') {", jenkins, StringComparison.Ordinal);
        Assert.Contains("powershell label: 'Deploy to Billing.Worker on the prod servers', script: '''", jenkins, StringComparison.Ordinal);
        Assert.Contains("unstash 'build'", jenkins, StringComparison.Ordinal);
        var rollback = Content(result, "Jenkinsfile.rollback");
        Assert.Contains("choice(name: 'ENVIRONMENT', choices: ['dev', 'test', 'prod']", rollback, StringComparison.Ordinal);
        Assert.Contains("expression { params.ENVIRONMENT == 'prod' }", rollback, StringComparison.Ordinal);

        var bitbucket = Content(result, BitbucketGenerator.Path);
        Assert.Contains("- self.hosted", bitbucket, StringComparison.Ordinal);
        Assert.Contains("- windows", bitbucket, StringComparison.Ordinal);
        Assert.Contains("stage:", bitbucket, StringComparison.Ordinal);
        Assert.Contains("rollback-prod:", bitbucket, StringComparison.Ordinal);
        Assert.Contains("Join-Path (Get-Location) 'out\\app'", bitbucket, StringComparison.Ordinal);

        var circle = Content(result, CircleCiGenerator.Path);
        Assert.Contains("resource_class: web1", circle, StringComparison.Ordinal);
        Assert.Contains("shell: powershell.exe -ExecutionPolicy Bypass", circle, StringComparison.Ordinal);
        Assert.Contains("rollback-prod:", circle, StringComparison.Ordinal);
        Assert.Contains("- none", circle, StringComparison.Ordinal);
    }

    [Fact]
    public void Tags_branches_and_settings_keep_their_meaning()
    {
        var tag = Spec(Target.ContainerApp, Stack.DotNet, Flow.Tag);
        tag.Environments[^1].Settings = [new() { Name = "Greeting", Value = "it's 5 o'clock" }, new() { Name = "Db", Secret = "DB_CONNECTION" }];
        var result = Generate(tag);
        Assert.Contains("tag 'v*'", Content(result, JenkinsGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("only: /^v.*$/", Content(result, CircleCiGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("ignore: /.*/", Content(result, CircleCiGenerator.Path), StringComparison.Ordinal);
        var bitbucket = YamlWriterTests.Parse(Content(result, BitbucketGenerator.Path));
        Assert.True(((YamlMappingNode)bitbucket.Children[new YamlScalarNode("pipelines")]).Children.ContainsKey(new YamlScalarNode("tags")));

        // A value is plain text for the shell; a secret is the variable of the same name.
        Assert.Contains("export CFG_1='it'\\''s 5 o'\\''clock'", Content(result, BitbucketGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("DB_CONNECTION = credentials('DB_CONNECTION_PROD')", Content(result, JenkinsGenerator.Path), StringComparison.Ordinal);

        var branches = Spec(Target.AppService, Stack.Node, Flow.BranchPerEnvironment);
        branches.PathFilters = ["web/**"];
        var perBranch = PipelineGenerator.Generate(branches);
        SchemaTests.AssertValid(perBranch);
        Assert.DoesNotContain(perBranch.Messages, message => message.Text.StartsWith("CircleCI filters paths", StringComparison.Ordinal));
        var setup = Content(perBranch, CircleCiGenerator.Path);
        Assert.Contains("setup: true", setup, StringComparison.Ordinal);
        Assert.Contains("path-filtering: circleci/path-filtering@1", setup, StringComparison.Ordinal);
        Assert.Contains("web/.* changed true", setup, StringComparison.Ordinal);
        Assert.Contains("when: << pipeline.parameters.changed >>", Content(perBranch, CircleCiGenerator.ContinuePath), StringComparison.Ordinal);
        branches.Schedule = "0 6 * * 1-5";
        Assert.Contains(PipelineRules.Check(branches), message => message.Text.StartsWith("CircleCI filters paths", StringComparison.Ordinal));
        branches.Schedule = null;
        Assert.Contains("changeset 'web/**'", Content(perBranch, JenkinsGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("includePaths:", Content(perBranch, BitbucketGenerator.Path), StringComparison.Ordinal);
        AssertGroovy(Content(perBranch, JenkinsGenerator.Path));
    }

    [Fact]
    public void What_a_system_cannot_do_is_said()
    {
        var several = Spec(Target.AppService);
        several.SeveralApps = true;
        several.PathFilters = ["src/**"];
        Assert.Contains(PipelineRules.Check(several), message => message.Severity == Severity.Error && message.Text.StartsWith("Bitbucket Pipelines and CircleCI read one file", StringComparison.Ordinal));
        several.Platforms = [Platform.Jenkins];
        Assert.Equal("Jenkinsfile-my-app", PipelineGenerator.Generate(several).Files[0].Path);

        var windows = Spec(Target.AppService);
        windows.Build.Agent = BuildAgent.HostedWindows;
        Assert.Contains(PipelineRules.Check(windows), message => message.Text.StartsWith("Bitbucket Pipelines has no hosted Windows", StringComparison.Ordinal));
    }

    /// <summary>
    /// The structure of a Jenkinsfile: every brace and parenthesis outside the strings closes, and every string ends.
    /// It is not Jenkins' own check (only a Jenkins server has that).
    /// </summary>
    internal static void AssertGroovy(string text)
    {
        var stack = new Stack<char>();
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                index = text.IndexOf('\n', index);
                Assert.True(index >= 0, "A comment does not end.");
            }
            else if (character == '\'')
            {
                var triple = string.CompareOrdinal(text, index, "'''", 0, 3) == 0;
                index += triple ? 3 : 1;
                while (true)
                {
                    Assert.True(index < text.Length, "A string does not end.");
                    if (text[index] == '\\')
                    {
                        index += 2;
                    }
                    else if (triple ? string.CompareOrdinal(text, index, "'''", 0, 3) == 0 : text[index] == '\'')
                    {
                        index += triple ? 2 : 0;
                        break;
                    }
                    else
                    {
                        Assert.True(triple || text[index] != '\n', "A single-quoted string runs over a line.");
                        index++;
                    }
                }
            }
            else if (character is '{' or '(' or '[')
            {
                stack.Push(character);
            }
            else if (character is '}' or ')' or ']')
            {
                Assert.True(stack.Count > 0, $"Nothing to close at {index}.");
                Assert.Equal(character switch { '}' => '{', ')' => '(', _ => '[' }, stack.Pop());
            }
        }

        Assert.Empty(stack);
        Assert.StartsWith("// Generated by", text, StringComparison.Ordinal);
        Assert.Contains("pipeline {", text, StringComparison.Ordinal);
    }
}
