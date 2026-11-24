using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class RulesTests
{
    [Fact]
    public void Every_example_is_complete()
    {
        foreach (var name in Examples.Names)
        {
            var spec = Examples.Get(name);
            spec.Platforms = [Platform.GitHubActions, Platform.AzureDevOps, Platform.GitLab];
            Assert.Empty(PipelineRules.Check(spec));
        }
    }

    [Fact]
    public void A_new_pipeline_starts_empty_and_is_not_listed_as_an_example()
    {
        var spec = Examples.Get(Examples.New);

        Assert.DoesNotContain(Examples.New, Examples.Names);
        Assert.Equal("", spec.Name);
        Assert.Equal("", spec.Repository);
        Assert.Equal("", spec.Build.ProjectPath);
        Assert.Empty(spec.Build.PackageFeeds);
        Assert.Equal(["dev", "test", "prod"], spec.Environments.Select(environment => environment.Name));
        Assert.All(spec.Environments, environment => Assert.True(string.IsNullOrEmpty(environment.ResourceName)));
        Assert.Contains(PipelineRules.Check(spec), message => message.Severity == Severity.Error);
    }

    [Theory]
    [InlineData("app; rm -rf /")]
    [InlineData("app $(whoami)")]
    [InlineData("app\nname")]
    [InlineData("app'name")]
    public void Values_that_could_break_a_command_are_refused(string value)
    {
        var spec = GenerationTests.Spec(Target.AppService);
        spec.Environments[0].ResourceName = value;

        var result = PipelineGenerator.Generate(spec);
        Assert.True(result.HasErrors);
        Assert.Empty(result.Files);
        Assert.Contains(result.Messages, message => message.Text == "Environment dev: write the web app name.");
    }

    [Fact]
    public void Azure_DevOps_needs_a_service_connection_for_each_environment()
    {
        var spec = GenerationTests.Spec(Target.Bicep, Stack.None);
        spec.Environments[1].ServiceConnection = null;

        Assert.Contains(PipelineRules.Check(spec), message => message.Text == "Environment test: write the Azure DevOps service connection it uses.");

        spec.Platforms = [Platform.GitHubActions];
        Assert.DoesNotContain(PipelineRules.Check(spec), message => message.Severity == Severity.Error);
    }

    [Fact]
    public void Names_and_environments_must_be_usable_as_job_names()
    {
        var spec = GenerationTests.Spec(Target.AppService);
        spec.Name = "My App";
        spec.Environments[1].Name = "dev";

        var messages = PipelineRules.Check(spec).Select(message => message.Text).ToList();
        Assert.Contains("The name uses lowercase letters, digits and dashes, starting with a letter (for example my-app).", messages);
        Assert.Contains("Environment dev appears twice.", messages);
    }

    [Fact]
    public void Nothing_to_deploy_is_an_error_and_an_unapproved_production_is_a_warning()
    {
        var spec = GenerationTests.Spec(Target.FunctionApp, Stack.None);
        spec.Environments[^1].RequireApproval = false;

        var messages = PipelineRules.Check(spec);
        Assert.Contains(messages, message => message is { Severity: Severity.Error, Text: "Choose what the code is written in, or write the build commands: there is nothing to deploy." });
        Assert.Contains(messages, message => message is { Severity: Severity.Warning, Text: "The last environment (prod) changes with no approval." });
    }

    [Fact]
    public void Containers_need_a_registry_and_an_image()
    {
        var spec = GenerationTests.Spec(Target.Aks, Stack.None);
        spec.Deploy.Registry = "my-registry.azurecr.io";
        spec.Deploy.ImageName = "Catalog";
        spec.Deploy.DeploymentName = null;

        var messages = PipelineRules.Check(spec).Select(message => message.Text).ToList();
        Assert.Contains("Write the Azure Container Registry name (letters and digits, without .azurecr.io).", messages);
        Assert.Contains("Write the image name in lowercase (for example my-app or team/my-app).", messages);
        Assert.Contains("AKS: write the namespace, the deployment and the container name (lowercase letters, digits and dashes).", messages);
    }

    [Fact]
    public void Terraform_needs_a_full_version_and_where_the_state_is_kept()
    {
        var spec = GenerationTests.Spec(Target.Terraform, Stack.None);
        spec.Deploy.TerraformVersion = "1.9";
        spec.Deploy.StateStorageAccount = "Has-Dashes";

        var messages = PipelineRules.Check(spec).Select(message => message.Text).ToList();
        Assert.Contains("Terraform: the version is a full release such as 1.9.8.", messages);
        Assert.Contains("Terraform: write where the state is kept (resource group, storage account in lowercase letters and digits, container).", messages);
    }
}
