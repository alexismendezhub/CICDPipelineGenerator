using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class StrategyTests
{
    private static readonly string[] Paths = [GitHubGenerator.Path, AzureDevOpsGenerator.Path, GitLabGenerator.Path];

    private static string Content(GenerationResult result, string path) => result.Files.Single(file => file.Path == path).Content;

    private static GenerationResult Generate(PipelineSpec spec)
    {
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        GenerationTests.AssertActionlint(Content(result, GitHubGenerator.Path));
        return result;
    }

    private static PipelineSpec Spec(Target target, DeployStrategy strategy)
    {
        var spec = GenerationTests.Spec(target, Stack.DotNet);
        spec.Deploy.Strategy = strategy;
        spec.Environments[^1].HealthCheckUrl = "https://shop.contoso.com/health";
        return spec;
    }

    [Theory]
    [InlineData(Target.AppService, "az webapp deployment slot swap --resource-group rg-contoso-prod --name app-my-app-prod --slot staging --target-slot production")]
    [InlineData(Target.FunctionApp, "az functionapp deployment slot swap --resource-group rg-contoso-prod --name app-my-app-prod --slot staging --target-slot production")]
    public void A_slot_is_swapped_into_production_after_its_health_check(Target target, string swap)
    {
        var spec = Spec(target, DeployStrategy.BlueGreen);
        spec.Environments[^1].Slot = "staging";
        var result = Generate(spec);
        foreach (var path in Paths)
        {
            var content = Content(result, path);
            Assert.Contains(swap, content, StringComparison.Ordinal);
            Assert.True(content.IndexOf("shop.contoso.com/health", StringComparison.Ordinal) < content.IndexOf("slot swap", StringComparison.Ordinal), path);
            Assert.DoesNotContain("traffic-routing", content, StringComparison.Ordinal);
        }

        // Only the environments with a slot swap.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Content(result, GitHubGenerator.Path), "slot swap"));

        spec.Deploy.Strategy = DeployStrategy.Canary;
        spec.Deploy.CanaryPercent = 25;
        var canary = Content(Generate(spec), AzureDevOpsGenerator.Path);
        Assert.Contains("az webapp traffic-routing set --resource-group rg-contoso-prod --name app-my-app-prod --distribution staging=25", canary, StringComparison.Ordinal);
        Assert.Contains("sleep 300", canary, StringComparison.Ordinal);
        CoverageTests.AssertBash([Strategies.SlotSwap(spec, spec.Environments[^1])!]);

        spec.Environments[^1].Slot = null;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("use a deployment slot", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DeployStrategy.BlueGreen)]
    [InlineData(DeployStrategy.Canary)]
    public void A_container_app_keeps_the_traffic_on_the_old_revision_until_the_new_one_is_healthy(DeployStrategy strategy)
    {
        var spec = Spec(Target.ContainerApp, strategy);
        var result = Generate(spec);
        foreach (var path in Paths)
        {
            var content = Content(result, path);
            Assert.Contains($"az containerapp revision set-mode --name {spec.Environments[^1].ResourceName} --resource-group {spec.Environments[^1].ResourceGroup} --mode multiple", content, StringComparison.Ordinal);
            Assert.Contains("--revision-weight \"$new=100\"", content, StringComparison.Ordinal);
            Assert.Contains("az containerapp revision deactivate", content, StringComparison.Ordinal);
            Assert.Equal(strategy == DeployStrategy.Canary, content.Contains("--revision-weight \"$old=90\" \"$new=10\"", StringComparison.Ordinal));
        }

        CoverageTests.AssertBash([Commands.ContainerAppUpdate(spec, spec.Environments[^1], "registry.azurecr.io/app:abc")]);
    }

    [Theory]
    [InlineData(Target.Aks)]
    [InlineData(Target.GcpGke)]
    [InlineData(Target.AwsEks)]
    [InlineData(Target.Kubernetes)]
    public void Kubernetes_switches_the_service_between_blue_and_green_or_updates_a_canary_first(Target target)
    {
        var spec = Spec(target, DeployStrategy.BlueGreen);
        var name = spec.Deploy.DeploymentName;
        var ns = spec.Deploy.Namespace;
        foreach (var path in Paths)
        {
            var content = Content(Generate(spec), path);
            Assert.Contains($"live=$(kubectl get service {name} -n {ns} -o jsonpath='{{.spec.selector.slot}}')", content, StringComparison.Ordinal);
            Assert.Contains($"kubectl rollout status -n {ns} deployment/{name}-$next --timeout=300s", content, StringComparison.Ordinal);
            Assert.Contains("--patch \"{\\\"spec\\\":{\\\"selector\\\":{\\\"slot\\\":\\\"$next\\\"}}}\"", content, StringComparison.Ordinal);
        }

        spec.Deploy.Strategy = DeployStrategy.Canary;
        spec.Deploy.CanaryMinutes = 2;
        var canary = Content(Generate(spec), GitHubGenerator.Path);
        Assert.Contains($"deployment/{name}-canary", canary, StringComparison.Ordinal);
        Assert.Contains("sleep 120", canary, StringComparison.Ordinal);
        Assert.True(canary.IndexOf($"deployment/{name}-canary", StringComparison.Ordinal) < canary.LastIndexOf($"kubectl set image -n {ns} deployment/{name} ", StringComparison.Ordinal));
        CoverageTests.AssertBash([Strategies.Kubernetes(spec, spec.Environments[^1], "\"r/app:abc\"")]);
        spec.Deploy.Strategy = DeployStrategy.BlueGreen;
        CoverageTests.AssertBash([Strategies.Kubernetes(spec, spec.Environments[^1], "\"r/app:abc\"")]);

        spec.Deploy.HelmChart = "charts/my-app";
        spec.Deploy.ContainerName = null;
        foreach (var path in Paths)
        {
            var content = Content(Generate(spec), path);
            Assert.Contains($"helm upgrade --install {name}-$next charts/my-app --namespace {ns}", content, StringComparison.Ordinal);
            Assert.Contains("app.kubernetes.io/instance", content, StringComparison.Ordinal);
        }

        spec.Deploy.Strategy = DeployStrategy.Canary;
        var helmCanary = Content(Generate(spec), GitLabGenerator.Path);
        Assert.Contains($"helm upgrade --install {name}-canary charts/my-app", helmCanary, StringComparison.Ordinal);
        Assert.Contains($"helm rollback {name}-canary -n {ns} --wait", helmCanary, StringComparison.Ordinal);
        CoverageTests.AssertBash([Strategies.Helm(spec, spec.Environments[^1], "\"r/app:abc\"")]);
        spec.Deploy.Strategy = DeployStrategy.BlueGreen;
        CoverageTests.AssertBash([Strategies.Helm(spec, spec.Environments[^1], "\"r/app:abc\"")]);
    }

    [Fact]
    public void Beanstalk_swaps_two_environments_and_servers_wait_after_the_first_one()
    {
        var beanstalk = Spec(Target.AwsElasticBeanstalk, DeployStrategy.BlueGreen);
        beanstalk.Environments[^1].Slot = "shop-prod-green";
        foreach (var path in Paths)
        {
            var content = Content(Generate(beanstalk), path);
            Assert.Contains("aws elasticbeanstalk update-environment --environment-name shop-prod-green --version-label \"$version\"", content, StringComparison.Ordinal);
            Assert.Contains("aws elasticbeanstalk swap-environment-cnames --source-environment-name shop-prod-green --destination-environment-name app-my-app-prod", content, StringComparison.Ordinal);
        }

        CoverageTests.AssertBash([CloudDeploy.Deploy(beanstalk, beanstalk.Environments[^1], Platform.GitLab, "abc")]);
        beanstalk.Deploy.Strategy = DeployStrategy.Canary;
        Assert.Contains(PipelineRules.Check(beanstalk), message => message.Text.StartsWith("Blue-green and canary deploys are for", StringComparison.Ordinal));

        var servers = GenerationTests.Spec(Target.WindowsService, Stack.DotNet);
        servers.Platforms = [.. GenerationTests.Six];
        servers.Deploy.Strategy = DeployStrategy.Canary;
        servers.Deploy.CanaryMinutes = 3;
        servers.Environments[^1].Servers = ["web1", "web2"];
        var result = PipelineGenerator.Generate(servers);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        Assert.Contains(result.Messages, message => message.Text.StartsWith("Azure DevOps updates the environment's machines", StringComparison.Ordinal));
        SchemaTests.AssertValid(result);
        var github = Content(result, GitHubGenerator.Path);
        GenerationTests.AssertActionlint(github);
        Assert.Contains("if: matrix.server == 'web1'", github, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Seconds 180", github, StringComparison.Ordinal);
        Assert.Contains("if ($env:SERVER -eq 'web1')", Content(result, GitLabGenerator.Path), StringComparison.Ordinal);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(Content(result, JenkinsGenerator.Path), "Start-Sleep -Seconds 180"));

        var remote = GenerationTests.Spec(Target.LinuxService, Stack.DotNet);
        remote.Deploy.Remote = true;
        remote.Deploy.Strategy = DeployStrategy.Canary;
        foreach (var environment in remote.Environments)
        {
            environment.ResourceName = "billing";
            environment.ServerLabel = null;
            environment.Hosts = ["app1.contoso.com", "app2.contoso.com"];
        }

        var script = ServerDeploy.Script(remote, remote.Environments[0], Platform.GitHubActions);
        Assert.Contains("if [ \"$server\" = \"app1.contoso.com\" ]; then echo", script, StringComparison.Ordinal);
        CoverageTests.AssertBash([script]);

        remote.Environments.ForEach(environment => environment.Hosts = ["app1.contoso.com"]);
        Assert.Contains(PipelineRules.Check(remote), message => message.Text.StartsWith("Canary on your servers updates the first server", StringComparison.Ordinal));
    }

    [Fact]
    public void Cloud_run_and_lambda_move_the_traffic_with_revisions_and_an_alias()
    {
        var run = Spec(Target.GcpCloudRun, DeployStrategy.Canary);
        foreach (var path in Paths)
        {
            var content = Content(Generate(run), path);
            Assert.Contains("--no-traffic --tag candidate --quiet", content, StringComparison.Ordinal);
            Assert.Contains("gcloud run services update-traffic app-my-app-prod --region us-central1 --to-tags candidate=10 --quiet", content, StringComparison.Ordinal);
            Assert.Contains("gcloud run services update-traffic app-my-app-prod --region us-central1 --to-latest --quiet", content, StringComparison.Ordinal);
            Assert.Contains("--to-revisions \"$old=100\"", content, StringComparison.Ordinal);
        }

        var lambda = Spec(Target.AwsLambda, DeployStrategy.Canary);
        lambda.Deploy.CanaryPercent = 5;
        foreach (var path in Paths)
        {
            var content = Content(Generate(lambda), path);
            Assert.Contains("version=$(aws lambda publish-version --function-name app-my-app-prod --query Version --output text)", content, StringComparison.Ordinal);
            Assert.Contains("--routing-config \"AdditionalVersionWeights={$version=0.05}\"", content, StringComparison.Ordinal);
            Assert.Contains("--function-version \"$version\" --routing-config 'AdditionalVersionWeights={}'", content, StringComparison.Ordinal);
        }

        Assert.Contains("aws lambda create-alias", Content(Generate(lambda), SetupGuide.Path), StringComparison.Ordinal);
        CoverageTests.AssertBash([Strategies.Lambda(lambda, lambda.Environments[^1]), Strategies.CloudRun(run, run.Environments[^1], "r/app:abc")]);

        lambda.Deploy.Strategy = DeployStrategy.BlueGreen;
        Assert.DoesNotContain("sleep", CloudDeploy.Deploy(lambda, lambda.Environments[^1], Platform.GitLab, "abc"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeployStrategy.BlueGreen, "CodeDeployDefault.ECSAllAtOnce")]
    [InlineData(DeployStrategy.Canary, "CodeDeployDefault.ECSCanary10Percent5Minutes")]
    public void Ecs_moves_the_traffic_with_codedeploy(DeployStrategy strategy, string config)
    {
        var spec = Spec(Target.AwsEcs, strategy);
        spec.Deploy.CanaryPercent = 10;
        spec.Deploy.CanaryMinutes = 5;
        var result = Generate(spec);
        foreach (var path in Paths)
        {
            var content = Content(result, path);
            Assert.Contains($"aws deploy create-deployment --application-name app-my-app-prod --deployment-group-name app-my-app-prod --deployment-config-name {config}", content, StringComparison.Ordinal);
            Assert.Contains("aws deploy wait deployment-successful", content, StringComparison.Ordinal);
            Assert.DoesNotContain("aws ecs update-service", content, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(result.Messages, message => message.Text.Contains("Canary on ECS", StringComparison.Ordinal));
        Assert.Contains("CODE_DEPLOY", Content(result, SetupGuide.Path), StringComparison.Ordinal);
        CoverageTests.AssertBash(GenerationTests.Six.Select(platform => CloudDeploy.Deploy(spec, spec.Environments[^1], platform, "abc")));

        spec.Deploy.Strategy = DeployStrategy.Standard;
        Assert.Contains("aws ecs update-service", CloudDeploy.Deploy(spec, spec.Environments[^1], Platform.GitLab, "abc"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_strategy_is_refused_where_the_target_cannot_split_traffic()
    {
        var spec = Spec(Target.StaticWebApp, DeployStrategy.Canary);
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.StartsWith("Blue-green and canary deploys are for", StringComparison.Ordinal));

        var app = Spec(Target.ContainerApp, DeployStrategy.Canary);
        app.Deploy.CanaryPercent = 100;
        Assert.Contains(PipelineRules.Check(app), message => message.Text.StartsWith("Canary: the share", StringComparison.Ordinal));
    }
}
