using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class InfraTests
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

    [Fact]
    public void An_arm_template_is_read_as_json_and_deployed_like_bicep()
    {
        var spec = GenerationTests.Spec(Target.ArmTemplate, Stack.None);
        var result = Generate(spec);
        foreach (var path in Paths)
        {
            var content = Content(result, path);
            Assert.Contains("python3 -m json.tool infra/azuredeploy.json > /dev/null", content, StringComparison.Ordinal);
            Assert.Contains("az deployment group what-if --resource-group rg-contoso-prod --template-file infra/azuredeploy.json", content, StringComparison.Ordinal);
            Assert.DoesNotContain("az bicep", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Cloudformation_shows_the_change_set_and_deploys_a_stack_per_environment()
    {
        var spec = GenerationTests.Spec(Target.CloudFormation, Stack.None);
        spec.Deploy.BicepTemplate = "infra/template.yaml";
        spec.Environments[^1].ParametersFile = "infra/prod.json";
        var result = Generate(spec);
        foreach (var path in Paths)
        {
            var content = Content(result, path);
            Assert.Contains("/tmp/cfn-lint/bin/cfn-lint infra/template.yaml", content, StringComparison.Ordinal);
            Assert.Contains("aws cloudformation deploy --stack-name app-my-app-prod --template-file infra/template.yaml --parameter-overrides file://infra/prod.json --capabilities CAPABILITY_NAMED_IAM --no-fail-on-empty-changeset", content, StringComparison.Ordinal);
            Assert.Contains("aws cloudformation describe-change-set --stack-name app-my-app-prod", content, StringComparison.Ordinal);
        }

        Assert.Contains("uses: aws-actions/configure-aws-credentials@v6", Content(result, GitHubGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("AWS_ACCESS_KEY_ID: $(AWS_ACCESS_KEY_ID_PROD)", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("assume-role-with-web-identity", Content(result, GitLabGenerator.Path), StringComparison.Ordinal);
        CoverageTests.AssertBash([Infra.Deploy(spec, spec.Environments[^1], "7"), Infra.Check(spec)!]);

        spec.Deploy.Preview = false;
        Assert.DoesNotContain("create-change-set", Content(Generate(spec), GitHubGenerator.Path), StringComparison.Ordinal);
        spec.Deploy.CloudRegion = null;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("Write the AWS region", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cdk_synthesises_and_deploys_each_stack_with_the_job_token_on_gitlab()
    {
        var spec = GenerationTests.Spec(Target.AwsCdk, Stack.None);
        var result = Generate(spec);
        foreach (var path in Paths)
        {
            var content = Content(result, path);
            Assert.Contains("npx cdk synth --quiet", content, StringComparison.Ordinal);
            Assert.Contains("npx cdk diff app-my-app-prod", content, StringComparison.Ordinal);
            Assert.Contains("npx cdk deploy app-my-app-prod --require-approval never", content, StringComparison.Ordinal);
        }

        var gitlab = Content(result, GitLabGenerator.Path);
        Assert.Contains("image: node:22", gitlab, StringComparison.Ordinal);
        Assert.Contains("AWS_WEB_IDENTITY_TOKEN_FILE=\"$CI_BUILDS_DIR/.aws-jwt\"", gitlab, StringComparison.Ordinal);
        Assert.DoesNotContain("pip install", gitlab, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CloudProvider.Azure)]
    [InlineData(CloudProvider.Aws)]
    [InlineData(CloudProvider.Gcp)]
    public void Pulumi_previews_and_updates_a_stack_on_each_cloud(CloudProvider cloud)
    {
        var spec = GenerationTests.Spec(Target.Pulumi, Stack.None);
        spec.Deploy.InfraCloud = cloud;
        spec.Deploy.CloudRegion = cloud == CloudProvider.Aws ? "us-east-1" : null;
        spec.Environments[^1].ResourceName = "contoso/prod";
        var result = Generate(spec);
        foreach (var path in Paths)
        {
            var content = Content(result, path);
            Assert.Contains("pulumi stack select contoso/prod", content, StringComparison.Ordinal);
            Assert.Contains("pulumi preview --diff", content, StringComparison.Ordinal);
            Assert.Contains("pulumi up --yes --skip-preview", content, StringComparison.Ordinal);
        }

        var github = Content(result, GitHubGenerator.Path);
        var azure = Content(result, AzureDevOpsGenerator.Path);
        var gitlab = Content(result, GitLabGenerator.Path);
        Assert.Contains("PULUMI_ACCESS_TOKEN: ${{ secrets.PULUMI_ACCESS_TOKEN }}", github, StringComparison.Ordinal);
        Assert.Contains("PULUMI_ACCESS_TOKEN: $(PULUMI_ACCESS_TOKEN)", azure, StringComparison.Ordinal);
        Assert.Contains("image: pulumi/pulumi:latest", gitlab, StringComparison.Ordinal);
        switch (cloud)
        {
            case CloudProvider.Azure:
                Assert.Contains("ARM_USE_OIDC: 'true'", github, StringComparison.Ordinal);
                Assert.DoesNotContain("azure/login", github, StringComparison.Ordinal);
                Assert.Contains("addSpnToEnvironment: true", azure, StringComparison.Ordinal);
                Assert.Contains("ARM_OIDC_TOKEN: $AZURE_JWT", gitlab, StringComparison.Ordinal);
                break;
            case CloudProvider.Aws:
                Assert.Contains("uses: aws-actions/configure-aws-credentials@v6", github, StringComparison.Ordinal);
                Assert.Contains("AWS_WEB_IDENTITY_TOKEN_FILE", gitlab, StringComparison.Ordinal);
                break;
            default:
                Assert.Contains("uses: google-github-actions/auth@v3", github, StringComparison.Ordinal);
                Assert.Contains("export GOOGLE_APPLICATION_CREDENTIALS=\"$AGENT_TEMPDIRECTORY/gcp.json\"", azure, StringComparison.Ordinal);
                Assert.Contains("\"type\":\"external_account\"", gitlab, StringComparison.Ordinal);
                Assert.Contains("export GOOGLE_PROJECT=\"$GCP_PROJECT_ID\"", gitlab, StringComparison.Ordinal);
                break;
        }

        CoverageTests.AssertBash(CloudDeploy.GitLabSignIn(spec).Append(Infra.Deploy(spec, spec.Environments[^1], "7")));
        spec.Environments[0].ResourceName = "";
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("write the Pulumi stack", StringComparison.Ordinal));
    }
}
