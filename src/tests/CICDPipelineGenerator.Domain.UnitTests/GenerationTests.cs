using System.Diagnostics;
using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class GenerationTests
{
    public static TheoryData<Target, Stack, Flow> Combinations()
    {
        var data = new TheoryData<Target, Stack, Flow>();
        foreach (var flow in Enum.GetValues<Flow>())
        {
            foreach (var target in Enum.GetValues<Target>())
            {
                Stack[] stacks = target switch
                {
                    Target.Terraform or Target.Bicep or Target.ArmTemplate or Target.CloudFormation or Target.AwsCdk or Target.Pulumi => [Stack.None],
                    Target.ContainerApp or Target.Aks => [Stack.DotNet, Stack.Node, Stack.Python, Stack.Java, Stack.None],
                    Target.Iis => [Stack.DotNet, Stack.Node],
                    Target.ScheduledTask or Target.LinuxService => [Stack.DotNet],
                    Target.Package => [Stack.DotNet, Stack.Node],
                    Target.DigitalOcean => [Stack.Node, Stack.Python],
                    Target.Database => [Stack.DotNet],
                    Target.ApiManagement or Target.LogicApp or Target.DataFactory => [Stack.None],
                    Target.Heroku => [Stack.DotNet, Stack.None],
                    Target.ServiceFabric => [Stack.DotNet],
                    Target.AwsS3 or Target.Netlify or Target.Vercel or Target.CloudflarePages or Target.FirebaseHosting => [Stack.Node],
                    Target.AwsSam => [Stack.Node, Stack.Python],
                    Target.AwsAppRunner or Target.GitOps => [Stack.DotNet, Stack.None],
                    Target.GooglePlay => [Stack.Android],
                    Target.AppStore => [Stack.Ios],
                    Target.MicrosoftStore => [Stack.Msix],
                    Target.AwsLambda => [Stack.DotNet, Stack.Node, Stack.Python],
                    Target.AwsElasticBeanstalk => [Stack.DotNet, Stack.Java],
                    Target.AwsEcs or Target.GcpCloudRun or Target.GcpGke or Target.AwsEks or Target.Kubernetes or Target.ContainerRegistry => [Stack.DotNet, Stack.None],
                    Target.GcpCloudFunctions or Target.GcpAppEngine => [Stack.Node, Stack.Python],
                    Target.Script => [Stack.DotNet, Stack.Node, Stack.None],
                    Target.AppService => [Stack.DotNet, Stack.Node, Stack.Python, Stack.Java, Stack.Gradle, Stack.Go, Stack.Ruby, Stack.Php, Stack.Rust, Stack.Cpp, Stack.Swift, Stack.Scala, Stack.Elixir, Stack.Flutter],
                    _ => [Stack.DotNet, Stack.Node, Stack.Python, Stack.Java],
                };
                foreach (var stack in stacks)
                {
                    data.Add(target, stack, flow);
                }
            }
        }

        return data;
    }

    /// <summary>The systems whose files hold every job; the ones added later split them or leave parts to the system.</summary>
    internal static readonly Platform[] Six = [Platform.GitHubActions, Platform.AzureDevOps, Platform.GitLab, Platform.Jenkins, Platform.Bitbucket, Platform.CircleCi];

    /// <summary>A complete spec for any target and stack, for every pipeline system.</summary>
    internal static PipelineSpec Spec(Target target, Stack stack = Stack.DotNet, Flow flow = Flow.Promotion)
    {
        var name = target switch
        {
            Target.StaticWebApp => "node-static-web-app",
            Target.ContainerApp => "container-app",
            Target.Aks => "aks",
            Target.Terraform => "terraform",
            Target.Bicep => "bicep",
            Target.WindowsService or Target.ScheduledTask or Target.LinuxService => "windows-service",
            Target.Iis => "iis-site",
            Target.Script => "script",
            _ => "dotnet-app-service",
        };
        var spec = Examples.Get(name);
        spec.Platforms = [Platform.GitHubActions, Platform.AzureDevOps, Platform.GitLab];

        // The setup script has its own tests; the others read the pipeline files.
        spec.SetupScript = false;
        spec.Flow = flow;
        spec.Deploy.Target = target;
        spec.Build.Stack = stack;
        spec.Build.ProjectPath ??= "src/App/App.csproj";
        if (target == Target.ArmTemplate)
        {
            spec.Deploy.BicepTemplate = "infra/azuredeploy.json";
        }

        if (spec.IsAws || spec.IsGcp)
        {
            spec.Deploy.CloudRegion = spec.IsAws ? "us-east-1" : "us-central1";
            spec.Deploy.Bucket = "contoso-versions";
            spec.Deploy.Registry = "apps";
            spec.Deploy.ImageName = "my-app";
            spec.Deploy.DeploymentName = "my-app";
            spec.Deploy.ContainerName = "my-app";
            spec.Deploy.FunctionRuntime = "nodejs22";
            spec.Deploy.FunctionEntryPoint = "handler";
        }

        if (target == Target.Database)
        {
            spec.Deploy.Migrations = MigrationKind.SqlScripts;
        }

        if (target == Target.ServiceFabric)
        {
            spec.Deploy.AppId = "OrdersType";
            spec.Deploy.FabricProject = "src/Orders/Orders.sfproj";
            spec.Build.Agent = BuildAgent.HostedWindows;
        }

        if (target == Target.ApiManagement)
        {
            spec.Deploy.AppId = "orders";
            spec.Deploy.BicepTemplate = "api/openapi.json";
        }

        if (target == Target.GitOps)
        {
            spec.Deploy.GitOpsRepository = "github.com/contoso/deployments";
            foreach (var environment in spec.Environments)
            {
                environment.ParametersFile = $"apps/my-app/{environment.Name}/deployment.yaml";
            }
        }

        if (target is Target.Kubernetes or Target.ContainerRegistry or Target.GitOps)
        {
            spec.Deploy.Registry = "contoso";
            spec.Deploy.ImageName = "my-app";
            spec.Deploy.DeploymentName = "my-app";
            spec.Deploy.ContainerName = "my-app";
        }

        if (spec.IsStore)
        {
            spec.Deploy.AppId = "com.contoso.app";
            spec.Build.Agent = stack == Stack.Ios ? BuildAgent.HostedMac : stack == Stack.Msix ? BuildAgent.HostedWindows : BuildAgent.HostedLinux;
            spec.Build.ProjectPath = stack == Stack.Ios ? "App.xcworkspace" : "src/App.Package/App.Package.wapproj";
            spec.Build.Scheme = "App";
            string[] tracks = ["internal", "beta", "production"];
            for (var index = 0; index < spec.Environments.Count; index++)
            {
                spec.Environments[index].ResourceName = tracks[Math.Min(index, 2)];
            }
        }

        if (target == Target.Package)
        {
            spec.Deploy.PublishFeed = new() { Kind = FeedKind.JFrog, Type = stack == Stack.Node ? FeedType.Npm : FeedType.NuGet, Url = "https://acme.jfrog.io", Feed = "releases" };
        }

        spec.Build.WorkingDirectory = stack is Stack.Node or Stack.Python ? "web" : ".";
        foreach (var environment in spec.Environments)
        {
            environment.ResourceName ??= $"res-{environment.Name}";
            environment.Slot = target == Target.AppService && environment.Name == "prod" ? "staging" : null;
        }

        return spec;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Every_combination_writes_valid_yaml_for_every_system(Target target, Stack stack, Flow flow)
    {
        var result = PipelineGenerator.Generate(Spec(target, stack, flow));

        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        Assert.Equal([GitHubGenerator.Path, AzureDevOpsGenerator.Path, GitLabGenerator.Path, SetupGuide.Path], result.Files.Select(file => file.Path));
        foreach (var file in result.Files.Where(file => file.Path.EndsWith(".yml", StringComparison.Ordinal)))
        {
            var root = YamlWriterTests.Parse(file.Content);
            Assert.True(root.Children.Count > 0, file.Path);
            Assert.DoesNotContain("\r", file.Content);
        }

        // A GitLab job named like a top-level keyword would be read as that keyword.
        var gitlab = YamlWriterTests.Parse(result.Files.Single(file => file.Path == GitLabGenerator.Path).Content);
        string[] keywords = ["default", "include", "image", "services", "cache", "before_script", "after_script", "variables", "pages"];
        Assert.DoesNotContain(gitlab.Children.Keys.Select(key => ((YamlScalarNode)key).Value), keywords.Contains);
    }

    [Fact]
    public void GitHub_promotes_through_the_environments_with_one_identity_each()
    {
        var spec = Spec(Target.AppService);
        var root = YamlWriterTests.Parse(GitHubGenerator.Generate(spec).Content);

        var on = Map(root, "on");
        Assert.Equal(["main"], Seq(Map(on, "push"), "branches"));
        Assert.True(on.Children.ContainsKey(new YamlScalarNode("workflow_dispatch")));
        Assert.Equal("write", Text(Map(root, "permissions"), "id-token"));

        var jobs = Map(root, "jobs");
        Assert.Equal(["build", "deploy-dev", "deploy-test", "deploy-prod"], jobs.Children.Keys.Select(key => ((YamlScalarNode)key).Value));
        var prod = Map(jobs, "deploy-prod");
        Assert.Equal("deploy-test", Text(prod, "needs"));
        Assert.Equal("prod", Text(prod, "environment"));
        Assert.Equal("github.event_name != 'pull_request' && github.ref == 'refs/heads/main'", Text(prod, "if"));
        var content = GitHubGenerator.Generate(spec).Content;
        Assert.Contains("uses: azure/webapps-deploy@v3", content);
        Assert.Contains("slot-name: staging", content);
        Assert.Contains("dotnet publish src/MyApp/MyApp.csproj -c Release -o out/app", content);
    }

    [Fact]
    public void Each_environment_can_deploy_from_its_own_branch()
    {
        var spec = Spec(Target.ContainerApp, Stack.Node, Flow.BranchPerEnvironment);

        var github = YamlWriterTests.Parse(GitHubGenerator.Generate(spec).Content);
        Assert.Equal(["develop", "release", "main"], Seq(Map(Map(github, "on"), "push"), "branches"));
        var dev = Map(Map(github, "jobs"), "deploy-dev");
        Assert.Equal("build", Text(dev, "needs"));
        Assert.EndsWith("refs/heads/develop'", Text(dev, "if"));

        var devops = AzureDevOpsGenerator.Generate(spec).Content;
        Assert.Contains("eq(variables['Build.SourceBranch'], 'refs/heads/release')", devops);
        Assert.Contains("az acr build --registry contosoregistry --image orders-api:$(Build.SourceVersion)", devops);

        var gitlab = YamlWriterTests.Parse(GitLabGenerator.Generate(spec).Content);
        Assert.Equal(["build", "build-image"], Seq(Map(gitlab, "deploy-prod"), "needs"));
    }

    [Fact]
    public void Azure_DevOps_uses_one_stage_and_service_connection_per_environment()
    {
        var spec = Spec(Target.Terraform, Stack.None);
        var content = AzureDevOpsGenerator.Generate(spec).Content;
        var root = YamlWriterTests.Parse(content);

        var stages = (YamlSequenceNode)root.Children[new YamlScalarNode("stages")];
        Assert.Equal(["Build", "Deploy_dev", "Deploy_test", "Deploy_prod"], stages.Children.Select(stage => Text((YamlMappingNode)stage, "stage")));
        Assert.Equal("Deploy_test", Text((YamlMappingNode)stages.Children[3], "dependsOn"));
        Assert.Contains("environment: prod", content);
        Assert.Contains("azureSubscription: azure-prod", content);
        Assert.Contains("addSpnToEnvironment: true", content);
        Assert.Contains("-backend-config=key=platform-prod.tfstate", content);
        Assert.Contains("terraform plan -input=false -var-file=env/prod.tfvars -out=tfplan", content);
        Assert.Contains("set -eo pipefail", content);
    }

    [Fact]
    public void GitLab_waits_for_someone_to_run_an_environment_that_needs_approval()
    {
        var spec = Spec(Target.AppService, Stack.Python);
        var root = YamlWriterTests.Parse(GitLabGenerator.Generate(spec).Content);

        var prod = Map(root, "deploy-prod");
        Assert.Equal(".azure", Text(prod, "extends"));
        Assert.Equal(["build", "deploy-test"], Seq(prod, "needs"));
        Assert.Equal("false", Text(prod, "allow_failure"));
        var rule = (YamlMappingNode)((YamlSequenceNode)prod.Children[new YamlScalarNode("rules")]).Children[0];
        Assert.Equal("manual", Text(rule, "when"));
        Assert.Equal("on_success", Text((YamlMappingNode)((YamlSequenceNode)Map(root, "deploy-dev").Children[new YamlScalarNode("rules")]).Children[0], "when"));

        var script = string.Join("\n", Seq(prod, "script"));
        Assert.Contains("--src-path app.zip --type zip --slot staging", script);
        Assert.Equal("api://AzureADTokenExchange", Text(Map(Map(Map(root, ".azure"), "id_tokens"), "AZURE_JWT"), "aud"));
    }

    [Fact]
    public void Kubernetes_deploys_the_new_image_and_waits_for_the_rollout()
    {
        var content = GitHubGenerator.Generate(Spec(Target.Aks, Stack.Java)).Content;

        Assert.Contains("kubectl set image -n catalog deployment/catalog catalog=contosoregistry.azurecr.io/catalog:${{ github.sha }}", content);
        Assert.Contains("kubelogin convert-kubeconfig -l azurecli", content);
        Assert.Contains("kubectl rollout status -n catalog deployment/catalog --timeout=300s", content);
    }

    [Fact]
    public void Bicep_previews_with_what_if_unless_turned_off()
    {
        var spec = Spec(Target.Bicep, Stack.None);
        Assert.Contains("az deployment group what-if --resource-group rg-contoso-prod", GitLabGenerator.Generate(spec).Content);

        spec.Deploy.Preview = false;
        Assert.DoesNotContain("what-if", GitLabGenerator.Generate(spec).Content);
    }

    [Fact]
    public void Custom_commands_replace_the_build()
    {
        var spec = Spec(Target.AppService);
        spec.Build.CustomCommands = ["make release", "cp -r dist/. out/app/"];

        var content = GitHubGenerator.Generate(spec).Content;
        Assert.Contains("make release\n", content);
        Assert.DoesNotContain("dotnet publish", content);
    }

    [Fact]
    public void The_setup_guide_names_the_federated_credentials_and_approvals()
    {
        var guide = SetupGuide.Generate(Spec(Target.ContainerApp)).Content;

        Assert.Contains("--subject \"$prefix:environment:prod\"", guide);
        Assert.Contains("sub_claim_prefix // \"repo:contoso/orders-api\"", guide);
        Assert.Contains("project_path:contoso/orders-api:ref_type:branch:ref:main", guide);
        Assert.Contains("`azure-prod` for **prod**", guide);
        Assert.Contains("subject `$prefix:ref:refs/heads/main`", guide);
        Assert.Contains("**Required reviewers**", guide);
    }

    [Fact]
    public void Static_web_apps_use_a_deployment_token_per_environment()
    {
        var spec = Spec(Target.StaticWebApp, Stack.Node);

        Assert.Contains("$(SWA_TOKEN_PROD)", AzureDevOpsGenerator.Generate(spec).Content);
        Assert.Contains("${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN }}", GitHubGenerator.Generate(spec).Content);
        Assert.Contains("cp -r web/dist/. out/app/", GitLabGenerator.Generate(spec).Content);
        Assert.Contains("Manage deployment token", SetupGuide.Generate(spec).Content);
    }

    /// <summary>The GitHub action only accepts skip_app_build; skip_api_build exists in the Azure DevOps task alone (first real run, 2026-10-26).</summary>
    [Fact]
    public void Static_web_apps_pass_only_inputs_the_GitHub_action_accepts()
    {
        var spec = Spec(Target.StaticWebApp, Stack.Node);

        Assert.DoesNotContain("skip_api_build", GitHubGenerator.Generate(spec).Content);
        Assert.Contains("skip_app_build: true", GitHubGenerator.Generate(spec).Content);
        Assert.Contains("skip_api_build: true", AzureDevOpsGenerator.Generate(spec).Content);
    }

    /// <summary>
    /// Every action the workflows use runs on Node 24 (the runtime each declares in its action.yml at that version, checked
    /// 2026-10-26), or in Docker; GitHub warns about Node 20 actions and removed Node 20 from its runners.
    /// </summary>
    [Theory]
    [MemberData(nameof(Combinations))]
    public void GitHub_workflows_use_only_actions_that_run_on_Node_24(Target target, Stack stack, Flow flow)
    {
        var content = GitHubGenerator.Generate(Spec(target, stack, flow)).Content;

        foreach (var used in System.Text.RegularExpressions.Regex.Matches(content, @"uses: (\S+)").Select(match => match.Groups[1].Value))
            Assert.Contains(used, Node24OrDocker);
    }

    // jfrog/setup-jfrog-cli@v5 declares node24 in its action.yml (checked 2026-11-21).
    internal static readonly string[] Node24OrDocker =
    [
        "actions/checkout@v5", "actions/setup-node@v5", "actions/setup-dotnet@v5", "actions/setup-java@v5", "actions/setup-python@v6",
        "actions/upload-artifact@v6", "actions/download-artifact@v7", "azure/login@v3", "azure/webapps-deploy@v3",
        "Azure/functions-action@v1", "Azure/static-web-apps-deploy@v1", "hashicorp/setup-terraform@v4", "jfrog/setup-jfrog-cli@v5", "actions/cache@v5",
        // configure-aws-credentials@v6, google auth@v3 and setup-gcloud@v3 declare node24 (checked 2026-11-22).
        "aws-actions/configure-aws-credentials@v6", "google-github-actions/auth@v3", "google-github-actions/setup-gcloud@v3",
        // setup-go@v6, ruby/setup-ruby@v1 and shivammathur/setup-php@v2 declare node24 (checked 2026-11-22).
        "actions/setup-go@v6", "ruby/setup-ruby@v1", "shivammathur/setup-php@v2",
        // erlef/setup-beam@v1 declares node24; subosito/flutter-action@v2 is composite and uses actions/cache@v5 (checked 2026-11-22).
        "erlef/setup-beam@v1", "subosito/flutter-action@v2",
        // github/codeql-action lists v4 as its latest major version (checked 2026-11-22).
        "github/codeql-action/init@v4", "github/codeql-action/analyze@v4",
    ];

    /// <summary>actionlint checks the workflows the way GitHub reads them: expressions, action inputs and job graph.</summary>
    [Theory]
    [MemberData(nameof(Combinations))]
    public void GitHub_workflows_pass_actionlint(Target target, Stack stack, Flow flow) =>
        AssertActionlint(GitHubGenerator.Generate(Spec(target, stack, flow)).Content);

    /// <summary>Checks a workflow with actionlint; without actionlint installed it checks nothing (and the scripts did not ask for it).</summary>
    internal static void AssertActionlint(string workflow)
    {
        var actionlint = FindActionlint();
        if (actionlint is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable("CICDPIPELINEGENERATOR_ACTIONLINT"));
            return;
        }

        var folder = Directory.CreateTempSubdirectory("cicdpipelinegenerator-");
        try
        {
            var path = Path.Combine(folder.FullName, "deploy.yml");
            File.WriteAllText(path, workflow);

            // Self-hosted runner labels are the person's own, so actionlint is told which ones the workflow uses.
            var jobs = (YamlMappingNode)YamlWriterTests.Parse(workflow).Children[new YamlScalarNode("jobs")];
            var labels = jobs.Children.Values.Cast<YamlMappingNode>()
                .Where(job => job.Children.ContainsKey(new YamlScalarNode("runs-on")))
                .Select(job => job.Children[new YamlScalarNode("runs-on")])
                .OfType<YamlSequenceNode>()
                .SelectMany(sequence => sequence.Children.Select(item => ((YamlScalarNode)item).Value!))
                .Concat(jobs.Children.Values.Cast<YamlMappingNode>()
                    .Where(job => job.Children.ContainsKey(new YamlScalarNode("strategy")))
                    .SelectMany(job => ((YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)job.Children[new YamlScalarNode("strategy")]).Children[new YamlScalarNode("matrix")]).Children[new YamlScalarNode("server")]).Children.Select(item => ((YamlScalarNode)item).Value!)))
                .Where(label => label is not ("self-hosted" or "windows" or "linux" or "windows-latest" or "ubuntu-latest") && !label.Contains("${{", StringComparison.Ordinal))
                .Distinct()
                .ToList();
            var config = Path.Combine(folder.FullName, "actionlint.yaml");
            File.WriteAllText(config, labels.Count == 0
                ? "self-hosted-runner:\n  labels: []\n"
                : "self-hosted-runner:\n  labels:\n" + string.Concat(labels.Select(label => $"    - {label}\n")));
            using var process = Process.Start(new ProcessStartInfo(actionlint, ["-shellcheck=", "-pyflakes=", "-no-color", "-config-file", config, path])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static string? FindActionlint()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "actionlint.exe" } : ["actionlint"];
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            folders.AddRange(Directory.GetDirectories(winget, "rhysd.actionlint*"));
        }

        return folders.SelectMany(folder => names.Select(name => Path.Combine(folder, name))).FirstOrDefault(File.Exists);
    }

    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node.Children[new YamlScalarNode(key)];

    private static string? Text(YamlMappingNode node, string key) => ((YamlScalarNode)node.Children[new YamlScalarNode(key)]).Value;

    private static List<string?> Seq(YamlMappingNode node, string key) =>
        ((YamlSequenceNode)node.Children[new YamlScalarNode(key)]).Children.Select(item => ((YamlScalarNode)item).Value).ToList();
}
