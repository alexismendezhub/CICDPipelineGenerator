namespace CICDPipelineGenerator.Domain;

/// <summary>
/// Infrastructure as code beyond Terraform and Bicep: CloudFormation, the AWS CDK and Pulumi. The commands are the
/// tools' own; how each pipeline system signs in to the cloud is the same as for that cloud's application targets.
/// </summary>
public static class Infra
{
    public const string PulumiToken = "PULUMI_ACCESS_TOKEN";

    private const string InstallPulumi = "command -v pulumi > /dev/null || { curl -fsSL https://get.pulumi.com | sh; }\nexport PATH=\"$HOME/.pulumi/bin:$PATH\"";

    /// <summary>What the check job runs before anything is deployed; null for the targets with their own check.</summary>
    public static string? Check(PipelineSpec spec)
    {
        var deploy = spec.Deploy;
        return deploy.Target switch
        {
            // cfn-lint in its own environment, so it installs the same way on an agent and in a plain image.
            Target.CloudFormation => $"python3 -m venv /tmp/cfn-lint\n/tmp/cfn-lint/bin/pip install --quiet cfn-lint\n/tmp/cfn-lint/bin/cfn-lint {deploy.BicepTemplate}",
            Target.AwsCdk => $"cd {deploy.InfraDirectory}\nnpm ci\nnpx cdk synth --quiet",
            Target.Pulumi => $"test -f {Commands.Join(deploy.InfraDirectory, "Pulumi.yaml")}",
            _ => null,
        };
    }

    /// <summary>The deploy of one environment, once the job is signed in to the cloud.</summary>
    public static string Deploy(PipelineSpec spec, EnvironmentSpec environment, string runId)
    {
        var deploy = spec.Deploy;
        var name = environment.ResourceName;
        switch (deploy.Target)
        {
            case Target.CloudFormation:
            {
                var template = deploy.BicepTemplate;
                var parameters = string.IsNullOrWhiteSpace(environment.ParametersFile) ? null : environment.ParametersFile;
                var lines = new List<string>();
                if (deploy.Preview)
                {
                    // A change set that is only shown: what the deploy would create, change and delete.
                    lines.Add($"change=\"{spec.Name}-{runId}\"");
                    lines.Add($"if aws cloudformation describe-stacks --stack-name {name} > /dev/null 2>&1; then type=UPDATE; else type=CREATE; fi");
                    lines.Add($"aws cloudformation create-change-set --stack-name {name} --change-set-name \"$change\" --change-set-type \"$type\" --template-body file://{template}{(parameters is null ? "" : $" --parameters file://{parameters}")} --capabilities CAPABILITY_NAMED_IAM --output text > /dev/null");
                    lines.Add($"aws cloudformation wait change-set-create-complete --stack-name {name} --change-set-name \"$change\" || echo \"Nothing changes.\"");
                    lines.Add($"aws cloudformation describe-change-set --stack-name {name} --change-set-name \"$change\" --query 'Changes[].ResourceChange.[Action,LogicalResourceId,ResourceType]' --output table");
                    lines.Add($"aws cloudformation delete-change-set --stack-name {name} --change-set-name \"$change\"");
                }

                lines.Add($"aws cloudformation deploy --stack-name {name} --template-file {template}{(parameters is null ? "" : $" --parameter-overrides file://{parameters}")} --capabilities CAPABILITY_NAMED_IAM --no-fail-on-empty-changeset");
                return string.Join('\n', lines);
            }

            case Target.Terraform:
                return $"{ShellPlan.InstallTerraform(spec)}\ncd {deploy.InfraDirectory}\n{Commands.TerraformDeploy(spec, environment)}";
            case Target.AwsCdk:
                return $"cd {deploy.InfraDirectory}\nnpm ci\n{(deploy.Preview ? $"npx cdk diff {name}\n" : "")}npx cdk deploy {name} --require-approval never";
            default:
                return string.Join('\n', new[]
                {
                    InstallPulumi,
                    spec.IsGcp ? "export GOOGLE_PROJECT=\"$GCP_PROJECT_ID\"" : null,
                    $"cd {deploy.InfraDirectory}",
                    "pulumi install",
                    $"pulumi stack select {name}",
                    deploy.Preview ? "pulumi preview --diff" : null,
                    "pulumi up --yes --skip-preview",
                }.Where(line => line is not null));
        }
    }

    /// <summary>Azure for a tool that signs in by itself with the pipeline's token (Pulumi, like Terraform): GitHub's variables.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GitHubAzureEnv =>
    [
        new("ARM_CLIENT_ID", "${{ vars.AZURE_CLIENT_ID }}"),
        new("ARM_TENANT_ID", "${{ vars.AZURE_TENANT_ID }}"),
        new("ARM_SUBSCRIPTION_ID", "${{ vars.AZURE_SUBSCRIPTION_ID }}"),
        new("ARM_USE_OIDC", "true"),
    ];

    /// <summary>The image a GitLab job (or any job that starts from an image) runs these tools in.</summary>
    public static string Image(PipelineSpec spec) => spec.Deploy.Target switch
    {
        Target.AwsCdk => "node:22",
        Target.Terraform => CloudDeploy.AwsImage,
        Target.Pulumi => "pulumi/pulumi:latest",
        _ => CloudDeploy.AwsImage,
    };
}
