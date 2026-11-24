using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class ScriptDeployTests
{
    private static PipelineSpec Spec()
    {
        var spec = Examples.Get("script");
        spec.Platforms = [Platform.GitHubActions, Platform.AzureDevOps, Platform.GitLab];
        return spec;
    }

    [Fact]
    public void The_commands_run_after_the_azure_sign_in_with_the_environment_name()
    {
        var spec = Spec();
        var github = GitHubGenerator.Generate(spec).Content;
        var prod = Map(Map(YamlWriterTests.Parse(github), "jobs"), "deploy-prod");
        Assert.Equal("prod", Text(Map(prod, "env"), "DEPLOY_ENVIRONMENT"));
        var steps = Steps(prod);
        Assert.Equal(["actions/checkout@v5", "actions/download-artifact@v7", "azure/login@v3"], steps.Take(3).Select(step => Text(step, "uses")));
        Assert.Contains("az storage blob upload-batch", Text(steps[3], "run"), StringComparison.Ordinal);

        var azure = AzureDevOpsGenerator.Generate(spec).Content;
        Assert.Contains("task: AzureCLI@2", azure, StringComparison.Ordinal);
        Assert.Contains("DEPLOY_ENVIRONMENT: prod", azure, StringComparison.Ordinal);

        var gitlab = Map(YamlWriterTests.Parse(GitLabGenerator.Generate(spec).Content), "deploy-prod");
        Assert.Equal(".azure", Text(gitlab, "extends"));
        Assert.Equal("prod", Text(Map(gitlab, "variables"), "DEPLOY_ENVIRONMENT"));
    }

    [Fact]
    public void Without_azure_the_commands_run_in_plain_bash_and_need_no_service_connection()
    {
        var spec = Spec();
        spec.Deploy.ScriptSignsInToAzure = false;
        foreach (var environment in spec.Environments)
        {
            environment.ServiceConnection = null;
        }

        Assert.DoesNotContain(PipelineRules.Check(spec), message => message.Severity == Severity.Error);
        Assert.DoesNotContain("azure/login", GitHubGenerator.Generate(spec).Content, StringComparison.Ordinal);
        Assert.DoesNotContain("AzureCLI@2", AzureDevOpsGenerator.Generate(spec).Content, StringComparison.Ordinal);
        var gitlab = Map(YamlWriterTests.Parse(GitLabGenerator.Generate(spec).Content), "deploy-prod");
        Assert.Equal("ubuntu:24.04", Text(gitlab, "image"));
        Assert.DoesNotContain("az identity create", SetupGuide.Generate(spec).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_server_the_commands_run_in_powershell_on_its_runners()
    {
        var spec = Spec();
        spec.Deploy.ScriptOnServer = true;
        spec.Deploy.ScriptSignsInToAzure = false;
        spec.Deploy.DeployCommands = ["& .\\deploy\\install.ps1 -Environment $env:DEPLOY_ENVIRONMENT"];
        foreach (var environment in spec.Environments)
        {
            environment.ServerLabel = $"batch-{environment.Name}";
        }

        Assert.DoesNotContain(PipelineRules.Check(spec), message => message.Severity == Severity.Error);
        var prod = Map(Map(YamlWriterTests.Parse(GitHubGenerator.Generate(spec).Content), "jobs"), "deploy-prod");
        Assert.Equal(["self-hosted", "windows", "batch-prod"], ((YamlSequenceNode)prod.Children[new YamlScalarNode("runs-on")]).Children.Select(item => ((YamlScalarNode)item).Value));
        Assert.Equal("powershell", Text(Steps(prod).Last(), "shell"));

        var azure = AzureDevOpsGenerator.Generate(spec).Content;
        var stage = ((YamlSequenceNode)YamlWriterTests.Parse(azure).Children[new YamlScalarNode("stages")]).Children.Cast<YamlMappingNode>().Single(item => Text(item, "stage") == "Deploy_prod");
        var job = (YamlMappingNode)((YamlSequenceNode)stage.Children[new YamlScalarNode("jobs")]).Children[0];
        var deploy = Steps(Map(Map(Map(job, "strategy"), "rolling"), "deploy"));
        Assert.Equal(spec.Deploy.DeployCommands[0], deploy.Select(step => Text(step, "powershell")).Single(script => script is not null));
        Assert.Contains("errorActionPreference: stop", azure, StringComparison.Ordinal);
        Assert.Contains("resourceType: VirtualMachine", azure, StringComparison.Ordinal);
        var gitlab = Map(YamlWriterTests.Parse(GitLabGenerator.Generate(spec).Content), "deploy-prod");
        Assert.StartsWith("$ErrorActionPreference = 'Stop'", ((YamlScalarNode)((YamlSequenceNode)gitlab.Children[new YamlScalarNode("script")]).Children[0]).Value, StringComparison.Ordinal);

        spec.Deploy.ScriptSignsInToAzure = true;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("Azure sign-in is only for hosted runners", StringComparison.Ordinal));
    }

    [Fact]
    public void A_script_needs_commands()
    {
        var spec = Spec();
        spec.Deploy.DeployCommands = [];
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("Write the deploy commands", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(GenerationTests.Combinations), MemberType = typeof(GenerationTests))]
    public void Commands_before_and_after_wrap_the_deploy_step_on_every_target(Target target, Stack stack, Flow flow)
    {
        var spec = GenerationTests.Spec(target, stack, flow);
        var environment = spec.Environments[^1];
        environment.BeforeDeploy = [spec.UsesPowerShell ? "Write-Host 'before'" : "echo before"];
        environment.AfterDeploy = [spec.UsesPowerShell ? "Write-Host 'after'" : "echo after"];

        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));

        var github = Steps(Map(Map(YamlWriterTests.Parse(result.Files.Single(file => file.Path == GitHubGenerator.Path).Content), "jobs"), GitHubGenerator.JobId(environment)));
        // Before comes ahead of the steps of the deploy (one on most targets, more where a tool is installed first).
        Assert.InRange(github.FindIndex(step => Text(step, "name") == "Before the deploy"), 0, github.Count - 3);
        Assert.Equal("After the deploy", Text(github[^1], "name"));
        Assert.Equal(spec.UsesPowerShell ? "powershell" : null, github[^1].Children.TryGetValue(new YamlScalarNode("shell"), out var shell) ? ((YamlScalarNode)shell).Value : null);

        var gitlab = Map(YamlWriterTests.Parse(result.Files.Single(file => file.Path == GitLabGenerator.Path).Content), GitLabGenerator.JobId(environment));
        var script = ((YamlSequenceNode)gitlab.Children[new YamlScalarNode("script")]).Children.Select(item => ((YamlScalarNode)item).Value!).ToList();
        Assert.Contains("before", script[0], StringComparison.Ordinal);
        Assert.Contains("after", script[^1], StringComparison.Ordinal);
        Assert.Equal(environment.Name, Text(Map(gitlab, "variables"), "DEPLOY_ENVIRONMENT"));

        Assert.Contains("displayName: Before the deploy", result.Files.Single(file => file.Path == AzureDevOpsGenerator.Path).Content, StringComparison.Ordinal);
        GenerationTests.AssertActionlint(result.Files.Single(file => file.Path == GitHubGenerator.Path).Content);
    }

    private static List<YamlMappingNode> Steps(YamlMappingNode job) =>
        ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToList();

    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node.Children[new YamlScalarNode(key)];

    private static string? Text(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? ((YamlScalarNode)value).Value : null;
}
