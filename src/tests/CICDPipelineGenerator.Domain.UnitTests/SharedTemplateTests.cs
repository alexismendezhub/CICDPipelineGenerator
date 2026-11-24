using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

/// <summary>A template kept in its own repository and the short file that calls it.</summary>
public class SharedTemplateTests
{
    public static TheoryData<Target, Stack> Shapes() => new()
    {
        { Target.AppService, Stack.DotNet },
        { Target.FunctionApp, Stack.Node },
        { Target.ContainerApp, Stack.DotNet },
        { Target.Aks, Stack.DotNet },
        { Target.Bicep, Stack.None },
        { Target.Terraform, Stack.None },
        { Target.AwsLambda, Stack.Python },
        { Target.AwsEcs, Stack.DotNet },
        { Target.GcpCloudRun, Stack.DotNet },
        { Target.Kubernetes, Stack.DotNet },
        { Target.Netlify, Stack.Node },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_pipeline_is_a_template_with_parameters_and_a_file_that_calls_it(Target target, Stack stack)
    {
        var spec = GenerationTests.Spec(target, stack);
        spec.TemplateRepository = "contoso/pipeline-templates";
        var whole = PipelineGenerator.Generate(GenerationTests.Spec(target, stack));
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        string Content(string path) => result.Files.Single(file => file.Path == path).Content;
        string Whole(string path) => whole.Files.Single(file => file.Path == path).Content;
        var last = spec.Environments[^1];
        var key = "resource_" + last.Name;

        // The files that call the templates, and Azure DevOps's template, follow the systems' schemas.
        SchemaTests.AssertValid(result);

        // No mark is left in the templates: each one became a parameter.
        Assert.Equal(3, result.Files.Count(file => file.Path.StartsWith(SharedTemplates.Folder, StringComparison.Ordinal)));
        foreach (var file in result.Files.Where(file => file.Path.StartsWith(SharedTemplates.Folder, StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("zzq", file.Content, StringComparison.OrdinalIgnoreCase);
        }

        // GitHub Actions: a reusable workflow.
        var template = Content(SharedTemplates.Folder + SharedTemplates.GitHubPath);
        var caller = Content(GitHubGenerator.Path);
        GenerationTests.AssertActionlint(template);
        GenerationTests.AssertActionlint(caller);
        var call = (YamlMappingNode)((YamlMappingNode)YamlWriterTests.Parse(caller)["jobs"])["pipeline"];
        Assert.Equal("contoso/pipeline-templates/.github/workflows/deploy-template.yml@main", call["uses"].ToString());
        Assert.Equal("inherit", call["secrets"].ToString());
        Assert.Equal(spec.Name, ((YamlMappingNode)call["with"])["name"].ToString());
        // Filled in with the caller's values, the template has the same jobs as the pipeline written whole.
        Assert.Equal(Jobs(Whole(GitHubGenerator.Path)), Jobs(Filled(template, (YamlMappingNode)call["with"], name => $"${{{{ inputs.{name} }}}}")));

        // Azure DevOps: extends a template of another repository.
        var azure = YamlWriterTests.Parse(Content(AzureDevOpsGenerator.Path));
        Assert.Equal("azure-pipelines-template.yml@templates", ((YamlMappingNode)azure["extends"])["template"].ToString());
        Assert.False(azure.Children.ContainsKey("stages"));
        var azureTemplate = Content(SharedTemplates.Folder + SharedTemplates.AzurePath);
        Assert.StartsWith("parameters:", azureTemplate[azureTemplate.IndexOf("\nparameters:", StringComparison.Ordinal)..].TrimStart('\n'), StringComparison.Ordinal);
        Assert.Equal(spec.Name, ((YamlMappingNode)((YamlMappingNode)azure["extends"])["parameters"])["name"].ToString());
        if (target == Target.AppService)
        {
            Assert.Contains($"appName: ${{{{ parameters.{key} }}}}", azureTemplate, StringComparison.Ordinal);
            Assert.Equal(last.ResourceName, ((YamlMappingNode)((YamlMappingNode)azure["extends"])["parameters"])[key].ToString());
        }

        // GitLab: an included file with inputs.
        var gitlab = YamlWriterTests.Parse(Content(GitLabGenerator.Path));
        var include = (YamlMappingNode)((YamlSequenceNode)gitlab["include"]).Children[0];
        Assert.Equal("contoso/pipeline-templates", include["project"].ToString());
        Assert.Equal("/gitlab/deploy-template.yml", include["file"].ToString());
        var gitlabTemplate = Content(SharedTemplates.Folder + SharedTemplates.GitLabPath);
        Assert.StartsWith("spec:\n  inputs:\n    name:\n", gitlabTemplate, StringComparison.Ordinal);
        var body = gitlabTemplate[(gitlabTemplate.IndexOf("---\n", StringComparison.Ordinal) + 4)..];
        Assert.True(YamlWriterTests.Parse(body).Children.Count > 2);
        if (target == Target.AppService)
        {
            Assert.Contains($"--name $[[ inputs.{key} ]]", body, StringComparison.Ordinal);
        }

        Assert.Contains("## Shared templates", Content(SetupGuide.Path), StringComparison.Ordinal);
    }

    /// <summary>The template with the caller's values in place of its parameters.</summary>
    private static string Filled(string template, YamlMappingNode values, Func<string, string> reference) =>
        values.Children.Aggregate(template, (text, pair) => text.Replace(reference(pair.Key.ToString()), pair.Value.ToString(), StringComparison.Ordinal));

    private static string Jobs(string text) => text[text.IndexOf("\njobs:", StringComparison.Ordinal)..];

    [Fact]
    public void Without_a_templates_repository_nothing_changes_and_other_systems_are_written_whole()
    {
        var spec = GenerationTests.Spec(Target.AppService);
        Assert.DoesNotContain(PipelineGenerator.Generate(spec).Files, file => file.Path.StartsWith(SharedTemplates.Folder, StringComparison.Ordinal));

        spec.Platforms = [Platform.GitHubActions, Platform.Jenkins];
        spec.TemplateRepository = "contoso/pipeline-templates";
        var result = PipelineGenerator.Generate(spec);
        Assert.Contains(result.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("the other systems get their whole pipeline", StringComparison.Ordinal));
        Assert.Contains("stage('Build and test')", result.Files.Single(file => file.Path == JenkinsGenerator.Path).Content, StringComparison.Ordinal);

        spec.TemplateRepository = "not a repository";
        Assert.Contains(PipelineGenerator.Generate(spec).Messages, message => message.Severity == Severity.Error && message.Text.StartsWith("Shared templates:", StringComparison.Ordinal));
    }
}
