using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

/// <summary>Test results for the languages whose test tool needs a reporter, and CodeQL.</summary>
public class MoreBuildTests
{
    [Theory]
    [InlineData(Stack.Node, "jest-junit")]
    [InlineData(Stack.Python, "--junitxml=")]
    [InlineData(Stack.Rust, "cargo nextest run")]
    [InlineData(Stack.Flutter, "junitreport:tojunit")]
    [InlineData(Stack.Ruby, "--format RspecJunitFormatter")]
    public void The_test_results_are_written_as_junit_xml(Stack stack, string text)
    {
        var spec = GenerationTests.Spec(Target.AppService, stack);
        spec.Platforms = [.. GenerationTests.Six];
        spec.Build.PublishTestResults = true;
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        GenerationTests.AssertActionlint(result.Files.Single(file => file.Path == GitHubGenerator.Path).Content);
        foreach (var file in result.Files.Where(file => file.Path != SetupGuide.Path))
        {
            Assert.Contains(text, file.Content, StringComparison.Ordinal);
            Assert.Contains("out/test-results/junit.xml", file.Content, StringComparison.Ordinal);
        }

        Assert.Contains("testResultsFormat: JUnit", result.Files.Single(file => file.Path == AzureDevOpsGenerator.Path).Content, StringComparison.Ordinal);
        Assert.Contains("junit:", result.Files.Single(file => file.Path == GitLabGenerator.Path).Content, StringComparison.Ordinal);
        CoverageTests.AssertBash(Commands.Build(spec).Select(step => step.Script));

        // On Windows the steps are PowerShell, and these reporters are set up with bash.
        spec.Build.Agent = BuildAgent.HostedWindows;
        spec.Platforms = [Platform.GitHubActions];
        Assert.Contains(PipelineRules.Check(spec), message => message.Severity == Severity.Error && message.Text.Contains("Test results are kept", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Stack.DotNet, "csharp", "none")]
    [InlineData(Stack.Node, "javascript-typescript", "none")]
    [InlineData(Stack.Go, "go", "autobuild")]
    [InlineData(Stack.Cpp, "c-cpp", "autobuild")]
    public void CodeQl_analyses_the_code_next_to_the_build(Stack stack, string language, string mode)
    {
        var spec = GenerationTests.Spec(Target.AppService, stack);
        spec.Platforms = [Platform.GitHubActions];
        spec.Build.CodeQl = true;
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        Assert.DoesNotContain(result.Messages, message => message.Text.Contains("CodeQL", StringComparison.Ordinal));
        var workflow = result.Files.Single(file => file.Path == GitHubGenerator.Path).Content;
        GenerationTests.AssertActionlint(workflow);

        var job = (YamlMappingNode)((YamlMappingNode)YamlWriterTests.Parse(workflow)["jobs"])["codeql"];
        Assert.Equal("write", ((YamlMappingNode)job["permissions"])["security-events"].ToString());
        var init = (YamlMappingNode)((YamlMappingNode)((YamlSequenceNode)job["steps"]).Children[1])["with"];
        Assert.Equal(language, init["languages"].ToString());
        Assert.Equal(mode, init["build-mode"].ToString());
        Assert.False(job.Children.ContainsKey("needs"));
        Assert.Contains("Code scanning", result.Files.Single(file => file.Path == SetupGuide.Path).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Where_codeql_is_not_written_it_is_said()
    {
        var swift = GenerationTests.Spec(Target.AppService, Stack.Swift);
        swift.Platforms = [Platform.GitHubActions];
        swift.Build.CodeQl = true;
        var none = PipelineGenerator.Generate(swift);
        Assert.Contains(none.Messages, message => message.Severity == Severity.Warning && message.Text.Contains("no analysis is written", StringComparison.Ordinal));
        Assert.DoesNotContain("codeql", none.Files[0].Content, StringComparison.Ordinal);

        var spec = GenerationTests.Spec(Target.AppService);
        spec.Build.CodeQl = true;
        Assert.Contains(PipelineGenerator.Generate(spec).Messages, message => message.Severity == Severity.Warning && message.Text.Contains("GitHub Actions only", StringComparison.Ordinal));
    }
}

/// <summary>The one-time setup as a script.</summary>
public class SetupScriptTests
{
    [Theory]
    [InlineData(Target.AppService, "Website Contributor", "/providers/Microsoft.Web/sites/app-my-app-prod")]
    [InlineData(Target.ContainerApp, "Contributor", "/providers/Microsoft.App/containerApps/")]
    [InlineData(Target.Aks, "Azure Kubernetes Service Cluster User Role", "/providers/Microsoft.ContainerService/managedClusters/")]
    [InlineData(Target.Bicep, "Contributor", "/resourceGroups/")]
    public void Github_and_azure_are_set_up_with_one_script(Target target, string role, string scope)
    {
        var spec = GenerationTests.Spec(target, GenerationTests.Combinations().First(row => (Target)row[0] == target)[1] is Stack stack ? stack : Stack.DotNet);
        spec.Platforms = [Platform.GitHubActions];
        spec.SetupScript = true;
        spec.Repository ??= "contoso/my-app";
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        var script = result.Files.Single(file => file.Path == SetupScript.Path).Content;
        CoverageTests.AssertBash([script]);
        Assert.DoesNotContain("\r", script);
        foreach (var environment in spec.Environments)
        {
            Assert.Contains($"gh api --method PUT \"repos/$REPO/environments/{environment.Name}\"", script, StringComparison.Ordinal);
            Assert.Contains($"--subject \"${{prefix}}:environment:{environment.Name}\"", script, StringComparison.Ordinal);
            Assert.Contains($"gh variable set AZURE_CLIENT_ID --repo \"$REPO\" --env {environment.Name}", script, StringComparison.Ordinal);
        }

        Assert.Contains($"--assignee-object-id \"$principal\" --assignee-principal-type ServicePrincipal --role \"{role}\"", script, StringComparison.Ordinal);
        ServerDeployTests.AssertParses([result.Files.Single(file => file.Path == SetupScript.PowerShellPath(SetupScript.Path)).Content]);
        Assert.Contains(scope, script, StringComparison.Ordinal);
        Assert.Contains($"bash {SetupScript.Path}", result.Files.Single(file => file.Path == SetupGuide.Path).Content, StringComparison.Ordinal);

        // What is missing is created: the resource group always, the plan and the web app for App Service.
        Assert.Contains($"az group show --name {spec.Environments[^1].ResourceGroup} > /dev/null 2>&1 || az group create", script, StringComparison.Ordinal);
        Assert.Equal(target == Target.AppService, script.Contains("az webapp create --name app-my-app-prod", StringComparison.Ordinal));
        if (target == Target.AppService)
        {
            Assert.Contains("--runtime \"DOTNETCORE:", script, StringComparison.Ordinal);
            Assert.Contains("--sku \"$SKU\"", script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(Target.AppService)]
    [InlineData(Target.ContainerApp)]
    [InlineData(Target.Bicep)]
    public void Azure_devops_and_gitlab_are_set_up_with_their_own_scripts(Target target)
    {
        var spec = GenerationTests.Spec(target, GenerationTests.Combinations().First(row => (Target)row[0] == target)[1] is Stack stack ? stack : Stack.DotNet);
        spec.SetupScript = true;
        spec.Repository ??= "contoso/my-app";
        var secrets = target != Target.Bicep;
        if (secrets)
        {
            spec.Environments[^1].Settings = [new() { Name = "ConnectionStrings:Default", Secret = "DB_CONNECTION" }];
        }

        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        var last = spec.Environments[^1];
        var azure = result.Files.Single(file => file.Path == SetupScript.AzureDevOpsPath).Content;
        var gitlab = result.Files.Single(file => file.Path == SetupScript.GitLabPath).Content;
        CoverageTests.AssertBash([azure, gitlab]);
        Assert.DoesNotContain("\r", azure + gitlab);

        Assert.Contains($"\"name\": \"{last.ServiceConnection}\", \"type\": \"AzureRM\"", azure, StringComparison.Ordinal);
        Assert.Contains("\"scheme\": \"WorkloadIdentityFederation\"", azure, StringComparison.Ordinal);
        Assert.Contains("--issuer \"$issuer\" --subject \"$subject\"", azure, StringComparison.Ordinal);

        // The pipeline alone may use the connection and the environment, so its first run does not wait for anyone.
        Assert.Contains("--area pipelinePermissions --resource pipelinePermissions", azure, StringComparison.Ordinal);
        Assert.Contains("resourceType=endpoint", azure, StringComparison.Ordinal);
        Assert.Contains("resourceType=environment", azure, StringComparison.Ordinal);

        // In PowerShell az is a .cmd file, which stops at a parenthesis inside an argument.
        Assert.DoesNotContain("length(", result.Files.Single(file => file.Path == SetupScript.PowerShellPath(SetupScript.AzureDevOpsPath)).Content, StringComparison.Ordinal);
        Assert.Contains($"{{ \"name\": \"{last.Name}\" }}", azure, StringComparison.Ordinal);
        ServerDeployTests.AssertParses(result.Files.Where(file => file.Path.StartsWith("setup/", StringComparison.Ordinal) && file.Path.EndsWith(".ps1", StringComparison.Ordinal)).Select(file => file.Content));
        Assert.Equal(6, result.Files.Count(file => file.Path.StartsWith("setup/", StringComparison.Ordinal)));
        if (secrets)
        {
            Assert.Contains($"az pipelines variable create --pipeline-name {spec.Name} --name {Secrets.AzureDevOpsName(last, "DB_CONNECTION")} --secret true", azure, StringComparison.Ordinal);
            Assert.Contains($"glab variable set DB_CONNECTION --value \"$value\" --repo \"$REPO\" --scope {last.Name} --masked", gitlab, StringComparison.Ordinal);
        }

        Assert.Contains($"--subject \"project_path:${{REPO}}:ref_type:branch:ref:{spec.BranchOf(last)}\"", gitlab, StringComparison.Ordinal);
        Assert.Contains($"glab variable set AZURE_CLIENT_ID --value \"$client\" --repo \"$REPO\" --scope {last.Name}", gitlab, StringComparison.Ordinal);

        var setup = result.Files.Single(file => file.Path == SetupGuide.Path).Content;
        Assert.Contains($"bash {SetupScript.AzureDevOpsPath}", setup, StringComparison.Ordinal);
        Assert.Contains($"bash {SetupScript.GitLabPath}", setup, StringComparison.Ordinal);

        // A tag changes with every release, so GitLab's credential is set as SETUP.md says.
        spec.Flow = Flow.Tag;
        Assert.DoesNotContain(PipelineGenerator.Generate(spec).Files, file => file.Path == SetupScript.GitLabPath);
    }

    [Fact]
    public void Secrets_are_asked_for_and_other_pairs_have_no_script()
    {
        var spec = GenerationTests.Spec(Target.AppService);
        spec.Platforms = [Platform.GitHubActions];
        spec.SetupScript = true;
        spec.Repository ??= "contoso/my-app";
        spec.Environments[^1].Settings = [new() { Name = "ConnectionStrings:Default", Secret = "DB_CONNECTION" }];
        var script = PipelineGenerator.Generate(spec).Files.Single(file => file.Path == SetupScript.Path).Content;
        Assert.Contains($"gh secret set DB_CONNECTION --repo \"$REPO\" --env {spec.Environments[^1].Name}", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--body \"$DB", script, StringComparison.Ordinal);

        spec.Platforms = [Platform.Jenkins];
        Assert.DoesNotContain(PipelineGenerator.Generate(spec).Files, file => file.Path.StartsWith("setup/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Target.AwsLambda, Stack.Python, "aws iam create-role --role-name my-app-prod-deploy", "arn:aws:iam::aws:policy/AWSLambda_FullAccess")]
    [InlineData(Target.AwsEcs, Stack.DotNet, "aws iam create-role --role-name my-app-build", "AmazonEC2ContainerRegistryPowerUser")]
    [InlineData(Target.GcpCloudRun, Stack.DotNet, "gcloud iam service-accounts create my-app-prod", "--role roles/run.admin")]
    [InlineData(Target.GcpCloudFunctions, Stack.Node, "gcloud iam workload-identity-pools providers create-oidc my-app", "roles/cloudfunctions.developer")]
    public void Github_is_set_up_for_aws_and_google_cloud_too(Target target, Stack stack, string creates, string grants)
    {
        var spec = GenerationTests.Spec(target, stack);
        spec.Platforms = [Platform.GitHubActions];
        spec.SetupScript = true;
        spec.Repository ??= "contoso/my-app";
        spec.Name = "my-app";
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        var bash = result.Files.Single(file => file.Path == SetupScript.Path).Content;
        var powerShell = result.Files.Single(file => file.Path == SetupScript.PowerShellPath(SetupScript.Path)).Content;
        CoverageTests.AssertBash([bash]);
        ServerDeployTests.AssertParses([powerShell]);
        foreach (var script in new[] { bash, powerShell })
        {
            Assert.Contains(creates, script, StringComparison.Ordinal);
            Assert.Contains(grants, script, StringComparison.Ordinal);
            Assert.Contains("${prefix}:environment:prod", script, StringComparison.Ordinal);
            Assert.Contains("--env prod --body", script, StringComparison.Ordinal);
        }

        // PowerShell stops when a command fails, as bash does with set -e.
        Assert.Contains("if ($LASTEXITCODE -ne 0) { throw", powerShell, StringComparison.Ordinal);
        Assert.DoesNotContain("/dev/null", powerShell, StringComparison.Ordinal);
    }
}
