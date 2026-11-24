using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class ContainerTests
{
    private static readonly Platform[] All = [Platform.GitHubActions, Platform.AzureDevOps, Platform.GitLab];

    private static string Content(GenerationResult result, string path) => result.Files.Single(file => file.Path == path).Content;

    private static GenerationResult Generate(PipelineSpec spec)
    {
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        GenerationTests.AssertActionlint(Content(result, GitHubGenerator.Path));
        return result;
    }

    [Fact]
    public void An_image_goes_to_docker_hub_and_every_environment_tags_it()
    {
        var spec = GenerationTests.Spec(Target.ContainerRegistry, Stack.DotNet);
        var result = Generate(spec);
        var github = Content(result, GitHubGenerator.Path);
        Assert.Contains("printf '%s' \"$REGISTRY_PASSWORD\" | docker login --username \"$REGISTRY_USER\" --password-stdin", github, StringComparison.Ordinal);
        Assert.Contains("docker push \"docker.io/contoso/my-app:${{ github.sha }}\"", github, StringComparison.Ordinal);
        Assert.Contains("docker buildx imagetools create --tag \"docker.io/contoso/my-app:prod\" \"docker.io/contoso/my-app:${{ github.sha }}\"", github, StringComparison.Ordinal);
        Assert.Contains("REGISTRY_PASSWORD: ${{ secrets.REGISTRY_PASSWORD }}", github, StringComparison.Ordinal);
        Assert.DoesNotContain("azure/login", github, StringComparison.Ordinal);
        Assert.DoesNotContain("aws-actions", github, StringComparison.Ordinal);

        Assert.Contains("REGISTRY_USER: $(REGISTRY_USER)", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        var gitlab = YamlWriterTests.Parse(Content(result, GitLabGenerator.Path));
        var image = (YamlMappingNode)gitlab.Children[new YamlScalarNode("build-image")];
        Assert.Equal("docker:27", ((YamlScalarNode)image.Children[new YamlScalarNode("image")]).Value);
        Assert.False(image.Children.ContainsKey(new YamlScalarNode("id_tokens")));
        Assert.Contains("## 1. Registry", Content(result, SetupGuide.Path), StringComparison.Ordinal);
        CoverageTests.AssertBash(All.Select(platform => CloudDeploy.Deploy(spec, spec.Environments[0], platform, "abc")).Append(CloudDeploy.BuildImage(spec, "abc")));
    }

    [Fact]
    public void Github_pushes_to_its_own_registry_with_its_own_token_and_another_registry_signs_in_to_its_address()
    {
        var spec = GenerationTests.Spec(Target.ContainerRegistry, Stack.None);
        spec.Deploy.RegistryKind = RegistryKind.GitHub;
        var github = Content(Generate(spec), GitHubGenerator.Path);
        Assert.Contains("packages: write", github, StringComparison.Ordinal);
        Assert.Contains("REGISTRY_USER: ${{ github.actor }}", github, StringComparison.Ordinal);
        Assert.Contains("REGISTRY_PASSWORD: ${{ secrets.GITHUB_TOKEN }}", github, StringComparison.Ordinal);
        Assert.Contains("docker login ghcr.io --username", github, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/contoso/my-app:", github, StringComparison.Ordinal);

        spec.Deploy.RegistryKind = RegistryKind.Other;
        spec.Deploy.Registry = "registry.contoso.com:5000/team";
        var gitlab = Content(Generate(spec), GitLabGenerator.Path);
        Assert.Contains("docker login registry.contoso.com:5000 --username", gitlab, StringComparison.Ordinal);
        Assert.Contains("registry.contoso.com:5000/team/my-app:$CI_COMMIT_SHA", gitlab, StringComparison.Ordinal);

        spec.Deploy.Registry = "Not A Registry";
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("Write the registry's address", StringComparison.Ordinal));
    }

    [Fact]
    public void Any_cluster_is_reached_with_its_kubeconfig_secret()
    {
        var spec = GenerationTests.Spec(Target.Kubernetes, Stack.DotNet);
        var result = Generate(spec);
        var github = Content(result, GitHubGenerator.Path);
        Assert.Contains("KUBE_CONFIG: ${{ secrets.KUBE_CONFIG }}", github, StringComparison.Ordinal);
        Assert.Contains("base64 -d > \"$HOME/.kube-config\"", github, StringComparison.Ordinal);
        Assert.Contains("kubectl set image -n default deployment/my-app my-app=\"docker.io/contoso/my-app:${{ github.sha }}\"", github, StringComparison.Ordinal);
        Assert.Contains("KUBE_CONFIG: $(KUBE_CONFIG_PROD)", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("image: python:3.12-slim", Content(result, GitLabGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("## 1. Registry and cluster", Content(result, SetupGuide.Path), StringComparison.Ordinal);
        CoverageTests.AssertBash(All.Select(platform => CloudDeploy.Deploy(spec, spec.Environments[0], platform, "abc")));

        spec.Deploy.ContainerName = null;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.StartsWith("Kubernetes: write the namespace", StringComparison.Ordinal));
    }

    [Fact]
    public void Eks_pushes_to_ecr_and_updates_the_cluster()
    {
        var spec = GenerationTests.Spec(Target.AwsEks, Stack.DotNet);
        var result = Generate(spec);
        foreach (var path in new[] { GitHubGenerator.Path, AzureDevOpsGenerator.Path, GitLabGenerator.Path })
        {
            var content = Content(result, path);
            Assert.Contains("aws eks update-kubeconfig --name app-my-app-prod --region \"$AWS_REGION\"", content, StringComparison.Ordinal);
            Assert.Contains("kubectl rollout status -n default deployment/my-app --timeout=300s", content, StringComparison.Ordinal);
            Assert.Contains("aws ecr get-login-password", content, StringComparison.Ordinal);
        }

        Assert.Contains("uses: aws-actions/configure-aws-credentials@v6", Content(result, GitHubGenerator.Path), StringComparison.Ordinal);
        CoverageTests.AssertBash(All.Select(platform => CloudDeploy.Deploy(spec, spec.Environments[0], platform, "abc")));
    }

    [Theory]
    [InlineData(Target.Aks)]
    [InlineData(Target.GcpGke)]
    [InlineData(Target.AwsEks)]
    [InlineData(Target.Kubernetes)]
    public void A_helm_chart_replaces_the_manifests_on_every_kubernetes_target(Target target)
    {
        var spec = GenerationTests.Spec(target, Stack.DotNet);
        spec.Deploy.HelmChart = "charts/my-app";
        spec.Deploy.ContainerName = null;
        spec.Environments[^1].ParametersFile = "charts/values-prod.yaml";
        var result = Generate(spec);
        foreach (var path in new[] { GitHubGenerator.Path, AzureDevOpsGenerator.Path, GitLabGenerator.Path })
        {
            var content = Content(result, path);
            Assert.Contains("helm upgrade --install " + spec.Deploy.DeploymentName + " charts/my-app --namespace " + spec.Deploy.Namespace + " --create-namespace -f charts/values-prod.yaml --set image.repository=\"${image%:*}\" --set image.tag=\"${image##*:}\" --wait --timeout 5m", content, StringComparison.Ordinal);
            Assert.DoesNotContain("kubectl set image", content, StringComparison.Ordinal);
            Assert.Contains("command -v helm > /dev/null", content, StringComparison.Ordinal);
        }

        CoverageTests.AssertBash([Kube.Rollout(spec, spec.Environments[^1], "\"registry/app:abc\""), Kube.InstallKubectl]);

        spec.Deploy.Target = Target.AppService;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.StartsWith("A Helm chart is for Kubernetes targets", StringComparison.Ordinal));
    }
}
