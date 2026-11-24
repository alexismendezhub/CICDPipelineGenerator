using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class MoreTargetsTests
{
    private static readonly Platform[] All = GenerationTests.Six;

    private static GenerationResult Generate(PipelineSpec spec)
    {
        spec.Platforms = [.. All];
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        GenerationTests.AssertActionlint(result.Files.Single(file => file.Path == GitHubGenerator.Path).Content);
        return result;
    }

    private static IEnumerable<GeneratedFile> Pipelines(GenerationResult result) => result.Files.Where(file => !file.Path.EndsWith(".md", StringComparison.Ordinal));

    [Theory]
    [InlineData(Target.Netlify, "npx --yes netlify-cli deploy --prod --dir", "NETLIFY_AUTH_TOKEN")]
    [InlineData(Target.Vercel, "npx --yes vercel deploy", "VERCEL_ORG_ID")]
    [InlineData(Target.CloudflarePages, "npx --yes wrangler pages deploy", "CLOUDFLARE_ACCOUNT_ID")]
    [InlineData(Target.DigitalOcean, "doctl apps create-deployment app-my-app-prod --wait", "DIGITALOCEAN_ACCESS_TOKEN")]
    [InlineData(Target.FirebaseHosting, "npx --yes firebase-tools deploy --only hosting --project app-my-app-prod --non-interactive", "GCP_PROJECT_ID")]
    public void A_site_goes_to_its_host_with_the_hosts_own_command_line(Target target, string command, string variable)
    {
        var spec = GenerationTests.Spec(target, Stack.Node);
        var result = Generate(spec);
        foreach (var file in Pipelines(result))
        {
            Assert.Contains(command, file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("azure/login", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("AZURE_CLIENT_SECRET", file.Content, StringComparison.Ordinal);
        }

        Assert.Contains(variable, result.Files.Single(file => file.Path == GitHubGenerator.Path).Content + result.Files.Single(file => file.Path == SetupGuide.Path).Content, StringComparison.Ordinal);
        CoverageTests.AssertBash(All.Select(platform => CloudDeploy.Deploy(spec, spec.Environments[^1], platform, "abc")));
    }

    [Fact]
    public void Sam_app_runner_and_gitops_use_the_commands_of_their_tools()
    {
        var sam = GenerationTests.Spec(Target.AwsSam, Stack.Python);
        foreach (var file in Pipelines(Generate(sam)))
        {
            Assert.Contains("/tmp/sam/bin/sam deploy --stack-name app-my-app-prod --resolve-s3 --capabilities CAPABILITY_IAM --no-confirm-changeset --no-fail-on-empty-changeset --region \"$AWS_REGION\"", file.Content, StringComparison.Ordinal);
        }

        var runner = GenerationTests.Spec(Target.AwsAppRunner, Stack.DotNet);
        foreach (var file in Pipelines(Generate(runner)))
        {
            Assert.Contains("ServiceSummaryList[?ServiceName=='app-my-app-prod'].ServiceArn | [0]", file.Content.Replace("\\'", "'", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("ImageRepositoryType=ECR", file.Content, StringComparison.Ordinal);
            Assert.Contains("aws ecr get-login-password", file.Content, StringComparison.Ordinal);
        }

        var gitOps = GenerationTests.Spec(Target.GitOps, Stack.DotNet);
        var result = Generate(gitOps);
        foreach (var file in Pipelines(result))
        {
            Assert.Contains("git clone --depth 1 \"https://x-access-token:$GITOPS_TOKEN@github.com/contoso/deployments\" \"$HOME/gitops\"", file.Content, StringComparison.Ordinal);
            Assert.Contains("apps/my-app/prod/deployment.yaml", file.Content, StringComparison.Ordinal);
            Assert.Contains("docker push \"docker.io/contoso/my-app:", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("kubectl", file.Content, StringComparison.Ordinal);
        }

        Assert.Contains("sed -i -E \"s#(docker.io/contoso/my-app):[A-Za-z0-9._-]+#\\1:abc#g\" apps/my-app/prod/deployment.yaml", CloudDeploy.Deploy(gitOps, gitOps.Environments[^1], Platform.GitLab, "abc"), StringComparison.Ordinal);
        Assert.Contains("Argo CD or Flux", result.Files.Single(file => file.Path == SetupGuide.Path).Content, StringComparison.Ordinal);
        CoverageTests.AssertBash(new[] { sam, runner, gitOps }.SelectMany(spec => All.Select(platform => CloudDeploy.Deploy(spec, spec.Environments[^1], platform, "abc"))));

        gitOps.Environments[0].ParametersFile = null;
        Assert.Contains(PipelineRules.Check(gitOps), message => message.Text.Contains("write the file of the manifests repository", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CloudProvider.Aws, "terraform init -input=false -backend-config=bucket=contoso-state -backend-config=key=NAME-prod.tfstate -backend-config=region=us-east-1")]
    [InlineData(CloudProvider.Gcp, "terraform init -input=false -backend-config=bucket=contoso-state -backend-config=prefix=NAME-prod")]
    public void Terraform_keeps_its_state_in_the_bucket_of_the_cloud_it_manages(CloudProvider cloud, string init)
    {
        var spec = GenerationTests.Spec(Target.Terraform, Stack.None);
        spec.Deploy.InfraCloud = cloud;
        spec.Deploy.Bucket = "contoso-state";
        spec.Deploy.CloudRegion = cloud == CloudProvider.Aws ? "us-east-1" : null;
        var result = Generate(spec);
        foreach (var file in Pipelines(result))
        {
            Assert.Contains(init.Replace("NAME", spec.Name, StringComparison.Ordinal), file.Content, StringComparison.Ordinal);
            Assert.Contains("terraform plan -input=false", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("ARM_", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("AZURE_CLIENT_SECRET", file.Content, StringComparison.Ordinal);
        }

        var github = result.Files.Single(file => file.Path == GitHubGenerator.Path).Content;
        Assert.Contains(cloud == CloudProvider.Aws ? "aws-actions/configure-aws-credentials@v6" : "google-github-actions/auth@v3", github, StringComparison.Ordinal);
        Assert.Contains(cloud == CloudProvider.Aws ? "backend \"s3\"" : "backend \"gcs\"", result.Files.Single(file => file.Path == SetupGuide.Path).Content, StringComparison.Ordinal);

        spec.Deploy.Bucket = null;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.StartsWith("Terraform: write the bucket", StringComparison.Ordinal));
    }

    [Fact]
    public void The_team_hears_about_a_deploy_and_a_tag_publishes_a_release()
    {
        var spec = GenerationTests.Spec(Target.AppService, Stack.DotNet, Flow.Tag);
        spec.NotifyOnSuccess = true;
        spec.CreateRelease = true;
        var result = Generate(spec);
        foreach (var file in Pipelines(result))
        {
            Assert.Contains("my-app is deployed to prod: ", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("failed: ", file.Content, StringComparison.Ordinal);
        }

        var github = result.Files.Single(file => file.Path == GitHubGenerator.Path).Content;
        Assert.Contains("gh release create \"$GITHUB_REF_NAME\" --repo \"$GITHUB_REPOSITORY\" --generate-notes --verify-tag", github, StringComparison.Ordinal);
        Assert.Contains("contents: write", github, StringComparison.Ordinal);
        var gitlab = result.Files.Single(file => file.Path == GitLabGenerator.Path).Content;
        Assert.Contains("tag_name: $CI_COMMIT_TAG", gitlab, StringComparison.Ordinal);
        Assert.Contains("NOTIFY_WEBHOOK = credentials('NOTIFY_WEBHOOK')", result.Files.Single(file => file.Path == JenkinsGenerator.Path).Content, StringComparison.Ordinal);
        Assert.Contains(result.Messages, message => message.Text.StartsWith("Releases are written for GitHub Actions and GitLab", StringComparison.Ordinal));
        foreach (var file in result.Files.Where(file => file.Path.StartsWith("Jenkinsfile", StringComparison.Ordinal)))
        {
            ShellSystemsTests.AssertGroovy(file.Content);
        }

        spec.Flow = Flow.Promotion;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.StartsWith("A release is published for a tag", StringComparison.Ordinal));
    }

    [Fact]
    public void Azure_services_heroku_and_a_database_alone_are_deployed_with_their_own_commands()
    {
        var api = GenerationTests.Spec(Target.ApiManagement, Stack.None);
        foreach (var file in Pipelines(Generate(api)))
        {
            Assert.Contains("az apim api import --resource-group rg-contoso-prod --service-name app-my-app-prod --api-id orders --path orders --specification-format OpenApi --specification-path api/openapi.json", file.Content, StringComparison.Ordinal);
        }

        var logic = Pipelines(Generate(GenerationTests.Spec(Target.LogicApp, Stack.None))).ToList();
        Assert.All(logic, file => Assert.Contains("az logicapp deployment source config-zip --resource-group rg-contoso-prod --name app-my-app-prod --src logicapp.zip", file.Content, StringComparison.Ordinal));
        var factory = Pipelines(Generate(GenerationTests.Spec(Target.DataFactory, Stack.None))).ToList();
        Assert.All(factory, file => Assert.Contains("--parameters factoryName=app-my-app-prod", file.Content, StringComparison.Ordinal));

        var fabric = GenerationTests.Spec(Target.ServiceFabric, Stack.DotNet);

        // Bitbucket has no hosted Windows, so the package is built on the person's own agent.
        fabric.Build.Agent = BuildAgent.SelfHostedWindows;
        fabric.Build.AgentLabel = "builders";
        foreach (var file in Pipelines(Generate(fabric)))
        {
            Assert.Contains("sfctl cluster select --endpoint \"$SF_CLUSTER_ENDPOINT\"", file.Content, StringComparison.Ordinal);
            Assert.Contains("sfctl application upgrade --application-id app-my-app-prod", file.Content, StringComparison.Ordinal);
            Assert.Contains("--app-type OrdersType", file.Content, StringComparison.Ordinal);
            Assert.Contains("src/Orders/Orders.sfproj -t:Package", file.Content, StringComparison.Ordinal);
            Assert.Contains("src/Orders/ApplicationParameters/*.xml", file.Content, StringComparison.Ordinal);
        }

        ServerDeployTests.AssertParses(Commands.Build(fabric).Select(step => step.Script));
        fabric.Build.Agent = BuildAgent.HostedLinux;
        Assert.Contains(PipelineRules.Check(fabric), message => message.Severity == Severity.Error && message.Text.Contains("Windows build agent", StringComparison.Ordinal));
        fabric.Build.Agent = BuildAgent.HostedWindows;

        CoverageTests.AssertBash(All.Select(platform => CloudDeploy.Deploy(fabric, fabric.Environments[^1], platform, "abc")));

        var heroku = GenerationTests.Spec(Target.Heroku, Stack.DotNet);
        foreach (var file in Pipelines(Generate(heroku)))
        {
            Assert.Contains("docker push registry.heroku.com/app-my-app-prod/web", file.Content, StringComparison.Ordinal);
            Assert.Contains("https://api.heroku.com/apps/app-my-app-prod/formation", file.Content, StringComparison.Ordinal);
        }

        CoverageTests.AssertBash(All.Select(platform => CloudDeploy.Deploy(heroku, heroku.Environments[^1], platform, "abc")));

        var database = GenerationTests.Spec(Target.Database, Stack.DotNet);
        foreach (var file in Pipelines(Generate(database)))
        {
            Assert.Contains("sqlcmd", file.Content, StringComparison.Ordinal);
            Assert.Contains("The database of prod is up to date.", file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("az webapp", file.Content, StringComparison.Ordinal);
        }

        database.Deploy.Migrations = MigrationKind.None;
        Assert.Contains(PipelineRules.Check(database), message => message.Text.StartsWith("Only the database: choose how", StringComparison.Ordinal));
    }

    [Fact]
    public void Tests_after_a_deploy_the_code_scan_and_the_review_are_written_where_asked()
    {
        var spec = GenerationTests.Spec(Target.AppService, Stack.Go);
        spec.Build.CodeScan = true;
        spec.Build.PublishTestResults = true;
        spec.Build.CachePackages = true;
        spec.Environments[1].EndToEndFolder = "e2e";
        spec.Environments[1].LoadTestScript = "tests/load.js";
        var result = Generate(spec);
        foreach (var file in Pipelines(result))
        {
            Assert.Contains("/tmp/semgrep/bin/semgrep scan --config auto --error --quiet .", file.Content, StringComparison.Ordinal);
            Assert.Contains("(cd e2e && npm ci && npx playwright install --with-deps chromium && npx playwright test)", file.Content, StringComparison.Ordinal);
            Assert.Contains("k6 run tests/load.js", file.Content, StringComparison.Ordinal);
            Assert.Contains("go-junit-report\" -set-exit-code > \"$root/out/test-results/junit.xml\"", file.Content, StringComparison.Ordinal);
        }

        var azure = result.Files.Single(file => file.Path == AzureDevOpsGenerator.Path).Content;
        Assert.Contains("testResultsFormat: JUnit", azure, StringComparison.Ordinal);
        Assert.Contains("GOMODCACHE", azure, StringComparison.Ordinal);
        Assert.Contains("go/pkg/mod", result.Files.Single(file => file.Path == BitbucketGenerator.Path).Content, StringComparison.Ordinal);
        CoverageTests.AssertBash(Commands.Build(spec).Select(step => step.Script).Append(Commands.Lines(Commands.After(spec.Environments[1]))));

        var ios = GenerationTests.Spec(Target.AppStore, Stack.Ios);
        ios.Deploy.SubmitForReview = true;
        var github = GitHubGenerator.Generate(ios).Content;
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(github, "fastlane deliver --app_identifier com.contoso.app"));
        Assert.Contains("--submit_for_review true --automatic_release false", github, StringComparison.Ordinal);
        ios.Deploy.Target = Target.GooglePlay;
        Assert.Contains(PipelineRules.Check(ios), message => message.Text.StartsWith("Sending the build to review", StringComparison.Ordinal));
    }

    [Fact]
    public void An_environment_only_changes_inside_its_deploy_window()
    {
        var spec = GenerationTests.Spec(Target.AppService, Stack.DotNet);
        spec.Environments[^1].DeployWindow = "1-5 08-18";
        foreach (var file in Pipelines(Generate(spec)))
        {
            Assert.Contains("[ \"$day\" -lt 1 ] || [ \"$day\" -gt 5 ] || [ \"$hour\" -lt 8 ] || [ \"$hour\" -ge 18 ]", file.Content, StringComparison.Ordinal);
            Assert.True(file.Content.IndexOf("-ge 18", StringComparison.Ordinal) < file.Content.LastIndexOf("app-my-app-prod", StringComparison.Ordinal), file.Path);
        }

        CoverageTests.AssertBash([DeployExtras.Window(spec, spec.Environments[^1])!.Script]);
        var server = GenerationTests.Spec(Target.WindowsService, Stack.DotNet);
        server.Environments[^1].DeployWindow = "1-5 08-18";
        ServerDeployTests.AssertParses([DeployExtras.Window(server, server.Environments[^1])!.Script]);
        Assert.Contains("$now.Hour -ge 18", GitHubGenerator.Generate(server).Content, StringComparison.Ordinal);

        spec.Environments[^1].DeployWindow = "5-1 18-08";
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("the deploy window is days and hours in UTC", StringComparison.Ordinal));
    }
}
