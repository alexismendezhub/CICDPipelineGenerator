using System.Text.RegularExpressions;

namespace CICDPipelineGenerator.Domain;

/// <summary>What Jenkins, Bitbucket Pipelines and CircleCI have in common: their jobs are shell scripts, and their secrets are environment variables.</summary>
public static partial class Systems
{
    public static bool IsShell(Platform platform) => platform is Platform.Jenkins or Platform.Bitbucket or Platform.CircleCi;

    public static string Name(Platform platform) => platform switch
    {
        Platform.GitHubActions => "GitHub Actions",
        Platform.AzureDevOps => "Azure DevOps",
        Platform.GitLab => "GitLab CI/CD",
        Platform.Jenkins => "Jenkins",
        Platform.Bitbucket => "Bitbucket Pipelines",
        Platform.TravisCi => "Travis CI",
        Platform.CloudBuild => "Google Cloud Build",
        Platform.CodePipeline => "AWS CodePipeline (CodeBuild)",
        Platform.TeamCity => "TeamCity",
        Platform.ArgoWorkflows => "Argo Workflows",
        _ => "CircleCI",
    };

    /// <summary>The commit being built, as the system's shell reads it.</summary>
    public static string Sha(Platform platform) => platform switch
    {
        Platform.Jenkins => "$GIT_COMMIT",
        Platform.Bitbucket => "$BITBUCKET_COMMIT",
        Platform.TravisCi => "$TRAVIS_COMMIT",
        Platform.CloudBuild => "$COMMIT_SHA",
        Platform.CodePipeline => "$CODEBUILD_RESOLVED_SOURCE_VERSION",
        // TeamCity and Argo Workflows: the job's script sets it first.
        Platform.TeamCity or Platform.ArgoWorkflows => "$GIT_COMMIT",
        _ => "$CIRCLE_SHA1",
    };

    public static string RunId(Platform platform) => platform switch
    {
        Platform.GitHubActions => "${{ github.run_number }}",
        Platform.AzureDevOps => "$BUILD_BUILDID",
        Platform.GitLab => "$CI_PIPELINE_IID",
        Platform.Jenkins => "$BUILD_NUMBER",
        Platform.Bitbucket => "$BITBUCKET_BUILD_NUMBER",
        Platform.TravisCi => "$TRAVIS_BUILD_NUMBER",
        Platform.CloudBuild => "$BUILD_ID",
        Platform.CodePipeline => "$CODEBUILD_BUILD_NUMBER",
        Platform.TeamCity => "$BUILD_NUMBER",
        Platform.ArgoWorkflows => "$GIT_COMMIT",
        _ => "$CIRCLE_BUILD_NUM",
    };

    public static string RunUrl(Platform platform) => platform switch
    {
        Platform.Jenkins => "$BUILD_URL",
        Platform.Bitbucket => "https://bitbucket.org/$BITBUCKET_REPO_FULL_NAME/pipelines/results/$BITBUCKET_BUILD_NUMBER",
        Platform.TravisCi => "$TRAVIS_BUILD_WEB_URL",
        Platform.CloudBuild => "https://console.cloud.google.com/cloud-build/builds/$BUILD_ID",
        Platform.CodePipeline => "$CODEBUILD_BUILD_URL",
        Platform.TeamCity or Platform.ArgoWorkflows => "",
        _ => "$CIRCLE_BUILD_URL",
    };

    /// <summary>The OpenID Connect token of the running job; Jenkins has none of its own, so it signs in with stored credentials.</summary>
    public static string? OidcToken(Platform platform) => platform switch
    {
        Platform.Bitbucket => "$BITBUCKET_STEP_OIDC_TOKEN",
        Platform.CircleCi => "$CIRCLE_OIDC_TOKEN_V2",
        _ => null,
    };

    /// <summary>
    /// The script with its variables set first. A value that names other variables ($FEED_USER) is read by the shell;
    /// any other value is written as plain text the shell does not look into.
    /// </summary>
    public static string WithEnv(string script, IReadOnlyList<KeyValuePair<string, string>> env, bool powerShell)
    {
        var lines = new List<string>();
        foreach (var (key, value) in env.Where(pair => pair.Value != "$" + pair.Key))
        {
            var reads = Reference().IsMatch(value);
            if (powerShell)
            {
                lines.Add(reads
                    ? $"$env:{key} = \"{Reference().Replace(value.Replace("`", "``", StringComparison.Ordinal).Replace("\"", "`\"", StringComparison.Ordinal), "$$($$env:$1)")}\""
                    : $"$env:{key} = '{value.Replace("'", "''", StringComparison.Ordinal)}'");
            }
            else
            {
                lines.Add(reads
                    ? $"export {key}=\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)}\""
                    : $"export {key}='{value.Replace("'", "'\\''", StringComparison.Ordinal)}'");
            }
        }

        return lines.Count == 0 ? script : string.Join('\n', lines) + "\n" + script;
    }

    /// <summary>The variables of the system's store a set of step variables reads.</summary>
    public static IEnumerable<string> Reads(IEnumerable<KeyValuePair<string, string>> env) =>
        env.SelectMany(pair => Reference().Matches(pair.Value).Select(match => match.Groups[1].Value));

    [GeneratedRegex(@"\$([A-Z][A-Z0-9_]*)")]
    private static partial Regex Reference();
}

/// <summary>One command of a job, with the variables it needs.</summary>
public sealed record PlanStep(string Name, string Script, IReadOnlyList<KeyValuePair<string, string>> Env);

/// <summary>A job as every shell-based system runs it; each writer turns it into its own syntax.</summary>
public sealed class PlanJob
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The environment the job deploys to (or rolls back); null for the build.</summary>
    public EnvironmentSpec? Environment { get; init; }

    public bool IsRollback { get; init; }

    /// <summary>The Docker image the job starts from, where the system uses one.</summary>
    public string Image { get; set; } = "ubuntu:24.04";

    /// <summary>The job builds or pushes images, so it needs a Docker daemon.</summary>
    public bool Docker { get; set; }

    /// <summary>The job signs in with the system's own OpenID Connect token.</summary>
    public bool Oidc { get; set; }

    /// <summary>The label of the person's own agent or runner (a build agent, or the environment's server).</summary>
    public string? Label { get; set; }

    /// <summary>The steps are Windows PowerShell.</summary>
    public bool Windows { get; set; }

    /// <summary>The job needs macOS (Xcode and Apple's upload tools).</summary>
    public bool Mac { get; set; }

    public bool Checkout { get; set; } = true;

    /// <summary>The job needs the files the build kept (out/app, out/migrations).</summary>
    public bool UsesBuild { get; set; }

    /// <summary>The folders later jobs need.</summary>
    public List<string> Keeps { get; } = [];

    /// <summary>The folders kept for people to download, also when the job fails (test results).</summary>
    public List<string> Results { get; } = [];

    /// <summary>JUnit XML files the system can show as test results.</summary>
    public string? JUnit { get; set; }

    /// <summary>The sign-in, repeated before each step where steps do not share a shell.</summary>
    public string Preamble { get; set; } = "";

    public List<PlanStep> Steps { get; } = [];

    /// <summary>Databases and caches started next to the job for its tests.</summary>
    public IReadOnlyList<ServiceDef> Services { get; set; } = [];

    /// <summary>The variables and secrets of the system's store the job reads.</summary>
    public List<string> Variables { get; } = [];

    /// <summary>Pull requests build and test but never push an image or deploy.</summary>
    public bool NotOnPullRequests { get; set; }

    /// <summary>The last deploy of the pipeline: when it works, the team is told.</summary>
    public bool NotifiesSuccess { get; set; }

    /// <summary>The language whose packages can be kept between runs, when that was asked for.</summary>
    public Stack? Cache { get; set; }

    public void Add(string name, string script, IReadOnlyList<KeyValuePair<string, string>>? env = null)
    {
        Steps.Add(new(name, script, env ?? []));
        Need(Systems.Reads(env ?? []));
    }

    public void Need(IEnumerable<string> names)
    {
        foreach (var name in names.Where(name => !Variables.Contains(name)))
        {
            Variables.Add(name);
        }
    }
}

public sealed record Plan(IReadOnlyList<PlanJob> Jobs, IReadOnlyList<PlanJob> Rollbacks)
{
    public IEnumerable<PlanJob> Deploys => Jobs.Where(job => job.Environment is not null);
}

/// <summary>
/// The jobs of a pipeline for the shell-based systems: the same builds and deploys the other systems get, written
/// with command lines only (az, aws, gcloud, kubectl, docker), so nothing depends on a system's own tasks or actions.
/// </summary>
public static partial class ShellPlan
{
    public const string AzureCliImage = "mcr.microsoft.com/azure-cli:latest";

    public static Plan Create(PipelineSpec spec, Platform platform)
    {
        var jobs = new List<PlanJob> { Build(spec, platform) };
        foreach (var (_, other) in Services.Versions(spec))
        {
            // One after another: what comes next runs only when the tests pass with every version.
            jobs.Add(Build(other, platform));
        }

        if (spec.IsContainer)
        {
            jobs.Add(ImageJob(spec, platform));
        }

        foreach (var environment in spec.Environments)
        {
            var servers = spec.OnServerRunner && environment.Servers.Count > 0 ? environment.Servers : [environment.ServerLabel ?? ""];
            jobs.AddRange(servers.Select(server => Deploy(spec, environment, platform, server, several: servers.Count > 1, rollback: false)));
        }

        jobs[^1].NotifiesSuccess = spec.NotifyOnSuccess && !jobs[^1].Windows;
        var rollbacks = new List<PlanJob>();
        if (spec.Deploy.RollbackPipeline && spec.IsServerApp)
        {
            foreach (var environment in spec.Environments)
            {
                var servers = spec.OnServerRunner && environment.Servers.Count > 0 ? environment.Servers : [environment.ServerLabel ?? ""];
                rollbacks.AddRange(servers.Select(server => Deploy(spec, environment, platform, server, several: servers.Count > 1, rollback: true)));
            }
        }

        return new(jobs, rollbacks);
    }

    private static PlanJob Build(PipelineSpec spec, Platform platform)
    {
        var deploy = spec.Deploy;
        var job = spec.Build.TestsOnly
            ? new PlanJob { Id = Services.Id(spec.Build.EffectiveVersion), Name = Services.Name(spec.Build.EffectiveVersion) }
            : new PlanJob { Id = "build", Name = spec.IsInfrastructure ? "Check" : "Build and test" };
        if (spec.IsInfrastructure)
        {
            switch (deploy.Target)
            {
                case Target.Terraform:
                    job.Image = AzureCliImage;
                    job.Add("Check the Terraform code", $"{InstallTerraform(spec)}\ncd {deploy.InfraDirectory}\n{Commands.TerraformCheck()}");
                    break;
                case Target.Bicep or Target.ArmTemplate:
                    job.Image = AzureCliImage;
                    job.Add(deploy.Target == Target.Bicep ? "Check the Bicep code" : "Check the template", (deploy.Target == Target.Bicep ? "az bicep install\n" : "") + Commands.BicepCheck(spec));
                    break;
                default:
                    job.Image = Infra.Image(spec);
                    job.Add("Check the infrastructure code", Infra.Check(spec)!);
                    break;
            }

            return job;
        }

        job.Windows = spec.IsWindowsBuild;
        job.Mac = spec.IsMacBuild;
        job.Label = spec.Build.Agent is BuildAgent.SelfHostedLinux or BuildAgent.SelfHostedWindows or BuildAgent.SelfHostedMac ? spec.Build.AgentLabel : null;
        job.Image = Commands.BuildImage(spec);
        // Cloud Build puts every step on one network, where a service answers by its name; elsewhere it is on localhost.
        var withServices = Services.Applies(spec);
        var byName = platform == Platform.CloudBuild;
        var ownDocker = platform is Platform.Jenkins or Platform.CodePipeline;
        IReadOnlyList<KeyValuePair<string, string>> feeds = withServices ? [.. Feeds.BuildEnv(spec, platform), .. Services.Env(spec, byName)] : Feeds.BuildEnv(spec, platform);
        if (withServices)
        {
            job.Services = Services.All(spec);
            if (ownDocker)
            {
                job.Add("Start the services", Services.Start(spec));
            }

            job.Add(Services.WaitName, Services.Wait(spec, byName)!);
        }

        if (Commands.ImagePreparation(spec) is { } prepare && platform != Platform.Jenkins)
        {
            job.Add("Install the tools", prepare);
        }

        if (Feeds.ConfigureScript(spec, platform) is { } configure)
        {
            job.Add("Configure package feeds", configure, Feeds.BuildEnv(spec, platform, forConfigure: true));
        }

        foreach (var step in Commands.Build(spec))
        {
            job.Add(step.Name, step.Script, feeds);
        }

        if (spec.UsesPackage)
        {
            job.Keeps.Add(Commands.PackageFolder);
        }

        if (DeployExtras.BuildMigrations(spec) is { } migrations)
        {
            job.Add(migrations.Name, migrations.Script, feeds);
            job.Keeps.Add(DeployExtras.MigrationsFolder);
        }

        if (spec.Build.PublishTestResults || spec.Build.Coverage)
        {
            if (spec.Build.WritesJUnit && spec.Build.PublishTestResults)
            {
                job.JUnit = Commands.JUnitFiles(spec);
            }
            else
            {
                job.Results.Add(Commands.TestResults);
            }
        }

        if (spec.Build.Sbom && Quality.Applies(spec))
        {
            job.Results.Add(Quality.Reports);
        }

        if (withServices && ownDocker)
        {
            job.Add("Stop the services", Services.Stop(spec));
        }

        if (job.Steps.Count == 0)
        {
            // Every system wants a job to do something; with nothing to compile, it says so.
            job.Add("Nothing to compile", "echo \"Nothing to compile here.\"");
        }

        job.Cache = PackageCache.Applies(spec) ? spec.Build.Stack : null;
        return job;
    }

    /// <summary>The image is built once, with the credentials kept for the whole repository; every environment deploys that image.</summary>
    private static PlanJob ImageJob(PipelineSpec spec, Platform platform)
    {
        var job = new PlanJob { Id = "build-image", Name = "Build and push the image", NotOnPullRequests = true };
        var sha = Systems.Sha(platform);
        if (spec.IsAzureContainer)
        {
            job.Image = AzureCliImage;
            SignInAzure(job, platform);
            job.Add(job.Name, Commands.BuildImage(spec, sha), Feeds.DockerEnv(spec, platform));
            return job;
        }

        job.Docker = spec.IsAws || spec.IsGenericRegistry;
        job.Image = spec.IsGcp ? CloudDeploy.GitLabImage(spec) : spec.IsAws ? (platform == Platform.CircleCi ? "cimg/python:3.12" : CloudDeploy.AwsImage) : "docker:27";
        SignInCloud(job, spec, platform, commandLine: true);
        job.Add(job.Name, CloudDeploy.BuildImage(spec, sha), CloudDeploy.Env(spec, null, platform));
        return job;
    }

    private static PlanJob Deploy(PipelineSpec spec, EnvironmentSpec environment, Platform platform, string server, bool several, bool rollback)
    {
        var deploy = spec.Deploy;
        var sha = Systems.Sha(platform);
        var job = new PlanJob
        {
            Id = (rollback ? "rollback-" : "deploy-") + environment.Name + (several ? "-" + Slug(server) : ""),
            Name = (rollback ? "Roll back " : "Deploy to ") + environment.Name + (several ? $" ({server})" : ""),
            Environment = environment,
            IsRollback = rollback,
            NotOnPullRequests = true,
            Checkout = false,
            UsesBuild = spec.UsesPackage && !rollback,
        };
        var deployEnv = DeployExtras.DeployEnv(spec, environment, platform);
        var resource = environment.ResourceName ?? environment.Name;
        if (rollback)
        {
            Server(job, spec, platform, server);
            job.Add("Put the previous version back", ServerDeploy.Script(spec, environment, platform, rollback: true), RemoteDeploy.Env(spec, environment, platform));
            return job;
        }

        switch (deploy.Target)
        {
            case Target.AppService:
            {
                job.Image = AzureCliImage;
                SignInAzure(job, platform);
                var slot = Slot(environment);
                job.Add($"Deploy to {resource}", spec.Build.IsJvm && spec.Build.CustomCommands.Count == 0
                    ? $"az webapp deploy --resource-group {environment.ResourceGroup} --name {resource} --src-path {Commands.PackagePattern(spec, Commands.PackageFolder)} --type jar{slot}"
                    : $"{Zip}\naz webapp deploy --resource-group {environment.ResourceGroup} --name {resource} --src-path app.zip --type zip{slot}");
                break;
            }

            case Target.FunctionApp:
                job.Image = AzureCliImage;
                SignInAzure(job, platform);
                job.Add($"Deploy to {resource}", $"{Zip}\naz functionapp deployment source config-zip --resource-group {environment.ResourceGroup} --name {resource} --src app.zip{Slot(environment)}");
                break;
            case Target.StaticWebApp:
                job.Image = "node:22";
                job.Add($"Deploy to {resource}", $"npx --yes @azure/static-web-apps-cli deploy {Commands.PackageFolder} --deployment-token \"$SWA_TOKEN\" --env production");
                job.Need(["SWA_TOKEN"]);
                break;
            case Target.ContainerApp:
                job.Image = AzureCliImage;
                SignInAzure(job, platform);
                job.Add($"Deploy to {resource}", Commands.ContainerAppUpdate(spec, environment, Commands.Image(spec, sha)), deployEnv);
                break;
            case Target.Aks:
                job.Image = AzureCliImage;
                job.Checkout = true;
                SignInAzure(job, platform);
                job.Add($"Deploy to {resource}", Commands.AksDeploy(spec, environment, Commands.Image(spec, sha)));
                break;
            case Target.Terraform when spec.NeedsAzure:
                job.Image = AzureCliImage;
                job.Checkout = true;
                SelfSignInAzure(job, platform);
                job.Add(deploy.Preview ? "Plan and apply" : "Apply", $"{InstallTerraform(spec)}\ncd {deploy.InfraDirectory}\n{Commands.TerraformDeploy(spec, environment, oidc: false)}");
                break;
            case Target.Bicep or Target.ArmTemplate:
                job.Image = AzureCliImage;
                job.Checkout = true;
                SignInAzure(job, platform);
                job.Add(deploy.Preview ? "Preview and deploy" : "Deploy", Commands.BicepDeploy(spec, environment, Systems.RunId(platform)));
                break;
            case Target.WindowsService or Target.Iis or Target.ScheduledTask or Target.LinuxService:
                Server(job, spec, platform, server);
                job.Add($"Deploy to {resource} on the {environment.Name} servers", ServerDeploy.Script(spec, environment, platform), deployEnv);
                break;
            case Target.Package:
            {
                job.Image = Commands.BuildImage(spec);
                var publish = Commands.PublishPackage(spec, platform, Commands.PackageFolder);
                job.Add(publish.Name, publish.Script, publish.Env);
                break;
            }

            case Target.Script or Target.ApiManagement or Target.LogicApp or Target.DataFactory:
                job.Checkout = true;
                if (spec.OnServerRunner)
                {
                    Server(job, spec, platform, server);
                }
                else if (spec.NeedsAzure)
                {
                    job.Image = AzureCliImage;
                    SignInAzure(job, platform);
                }

                job.Add("Deploy", Commands.Lines(Commands.DeployLines(spec, environment)), deployEnv);
                break;
            default:
                // AWS, Google Cloud, any registry or cluster, and the infrastructure tools that are not Azure's own.
                job.Checkout = CloudDeploy.NeedsRepository(spec);
                job.Image = CloudDeploy.GitLabImage(spec);
                job.Docker = deploy.Target == Target.Heroku;
                if (spec.DeploysOnMac)
                {
                    job.Mac = true;
                    job.Label = spec.Build.Agent == BuildAgent.SelfHostedMac ? spec.Build.AgentLabel : null;
                }

                if (spec.NeedsAzure)
                {
                    SelfSignInAzure(job, platform);
                }
                else
                {
                    SignInCloud(job, spec, platform, commandLine: !spec.SignsInWithLibraries);
                }

                job.Add($"Deploy to {resource}", CloudDeploy.Deploy(spec, environment, platform, sha), CloudDeploy.Env(spec, environment, platform));
                break;
        }

        // Before the deploy step, in order: the person's own commands, the database, the settings.
        var before = new List<PlanStep>();
        if (environment.BeforeDeploy.Count > 0)
        {
            before.Add(new("Before the deploy", Commands.Lines(environment.BeforeDeploy), []));
        }

        foreach (var extra in DeployExtras.Before(spec, environment, platform))
        {
            before.Add(new(extra.Name, extra.Script, extra.Env));
            job.Need(Systems.Reads(extra.Env));
        }

        job.Steps.InsertRange(job.Steps.Count - 1, before);
        if (deploy.Migrations == MigrationKind.SqlScripts || Commands.TestsAfter(environment))
        {
            job.Checkout = true;
        }

        job.UsesBuild |= deploy.Migrations == MigrationKind.EfCoreBundle;
        if (spec.OnServerRunner && several && Strategies.ServerPause(spec, environment) is { } pause && Strategies.FirstServer(spec, environment) == server)
        {
            job.Add("Let the first server run the new version before the others change", pause);
        }

        if (Strategies.SlotSwap(spec, environment) is { } swap)
        {
            job.Add($"Move the traffic to the new version ({environment.Slot} to production)", swap);
        }

        if (Commands.After(environment).Count > 0)
        {
            job.Add("After the deploy", Commands.Lines(Commands.After(environment)));
        }

        if (Commands.HasOwnCommands(spec, environment))
        {
            // Their own commands get the environment's name, in every step.
            var named = job.Steps.Select(step => step with { Env = [.. step.Env, new("DEPLOY_ENVIRONMENT", environment.Name)] }).ToList();
            job.Steps.Clear();
            job.Steps.AddRange(named);
        }

        return job;
    }

    /// <summary>The job runs on the environment's own servers, or reaches them from outside over SSH or WinRM.</summary>
    private static void Server(PlanJob job, PipelineSpec spec, Platform platform, string server)
    {
        job.Windows = spec.UsesPowerShell;
        if (spec.OnServerRunner)
        {
            job.Label = server;
            return;
        }

        job.Need(RemoteDeploy.SecretNames(spec));
        if (!spec.UsesPowerShell)
        {
            job.Image = "ubuntu:24.04";
            if (platform != Platform.Jenkins)
            {
                job.Add("Install the tools", "apt-get update -qq && apt-get install -y -qq openssh-client rsync python3 curl > /dev/null");
            }
        }
    }

    private static void SignInAzure(PlanJob job, Platform platform)
    {
        // Microsoft Entra matches a federated credential's subject exactly, and the tokens of these systems carry a
        // subject that changes with every step or user, so they sign in to Azure with the service principal's secret.
        _ = platform;
        job.Need(["AZURE_CLIENT_ID", "AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID", "AZURE_CLIENT_SECRET"]);
        job.Preamble = "az login --service-principal --username \"$AZURE_CLIENT_ID\" --password \"$AZURE_CLIENT_SECRET\" --tenant \"$AZURE_TENANT_ID\" --output none\naz account set --subscription \"$AZURE_SUBSCRIPTION_ID\"";
    }

    /// <summary>Terraform and Pulumi sign in to Azure by themselves, from these variables.</summary>
    private static void SelfSignInAzure(PlanJob job, Platform platform)
    {
        _ = platform;
        job.Need(["AZURE_CLIENT_ID", "AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID", "AZURE_CLIENT_SECRET"]);
        job.Preamble = "export ARM_CLIENT_ID=\"$AZURE_CLIENT_ID\" ARM_TENANT_ID=\"$AZURE_TENANT_ID\" ARM_SUBSCRIPTION_ID=\"$AZURE_SUBSCRIPTION_ID\" ARM_CLIENT_SECRET=\"$AZURE_CLIENT_SECRET\"";
    }

    /// <summary>AWS and Google Cloud; commandLine says the job runs aws or gcloud itself (the CDK and Pulumi only need the credentials).</summary>
    private static void SignInCloud(PlanJob job, PipelineSpec spec, Platform platform, bool commandLine)
    {
        var token = Systems.OidcToken(platform);
        var lines = new List<string>();
        if (spec.IsAws)
        {
            if (token is null)
            {
                job.Need(["AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY"]);
            }
            else
            {
                job.Oidc = true;
                job.Need(["AWS_ROLE_ARN"]);
                lines.Add($"printf '%s' \"{token}\" > \"$HOME/.aws-web-identity\"");
                lines.Add("export AWS_WEB_IDENTITY_TOKEN_FILE=\"$HOME/.aws-web-identity\" AWS_ROLE_SESSION_NAME=pipeline");
            }

            if (commandLine && platform != Platform.Jenkins)
            {
                lines.Add(job.Image.StartsWith("docker:", StringComparison.Ordinal) ? "command -v aws > /dev/null || apk add --no-cache aws-cli > /dev/null" : "command -v aws > /dev/null || pip install --quiet awscli");
            }
        }
        else if (spec.IsGcp)
        {
            job.Need(["GCP_PROJECT_ID"]);
            if (token is null)
            {
                job.Need(["GCP_CREDENTIALS"]);
                lines.Add("printf '%s' \"$GCP_CREDENTIALS\" > \"$HOME/.gcp.json\"");
                lines.Add("export GOOGLE_APPLICATION_CREDENTIALS=\"$HOME/.gcp.json\"");
                if (commandLine)
                {
                    lines.Add("gcloud auth activate-service-account --key-file=\"$HOME/.gcp.json\" --quiet");
                }
            }
            else
            {
                job.Oidc = true;
                job.Need(["GCP_WORKLOAD_IDENTITY_PROVIDER", "GCP_SERVICE_ACCOUNT"]);
                lines.Add($"printf '%s' \"{token}\" > \"$HOME/.gcp-token\"");
                lines.Add(CloudDeploy.ExternalAccount("$HOME/.gcp-token", "$HOME/.gcp.json"));
                lines.Add("export GOOGLE_APPLICATION_CREDENTIALS=\"$HOME/.gcp.json\"");
                if (commandLine)
                {
                    lines.Add("gcloud auth login --cred-file=\"$HOME/.gcp.json\" --quiet");
                }
            }

            if (commandLine)
            {
                lines.Add("gcloud config set project \"$GCP_PROJECT_ID\" --quiet");
            }
        }

        // Cloud Build already runs as a Google service account, and CodeBuild with the role of its project.
        if ((platform == Platform.CloudBuild && spec.IsGcp) || (platform == Platform.CodePipeline && spec.IsAws))
        {
            job.Variables.RemoveAll(name => name is "GCP_PROJECT_ID" or "GCP_CREDENTIALS" or "AWS_ACCESS_KEY_ID" or "AWS_SECRET_ACCESS_KEY");
            lines.RemoveAll(line => line.Contains("GCP_CREDENTIALS", StringComparison.Ordinal) || line.Contains(".gcp.json", StringComparison.Ordinal));
            if (spec.IsGcp)
            {
                lines.Insert(0, "export GCP_PROJECT_ID=\"$PROJECT_ID\"");
            }
        }

        job.Preamble = string.Join('\n', lines);
    }

    /// <summary>Terraform in the home folder, downloaded with Python: the images used here have it, and not always curl and unzip.</summary>
    public static string InstallTerraform(PipelineSpec spec)
    {
        var version = spec.Deploy.TerraformVersion;
        return string.Join('\n',
        [
            "mkdir -p \"$HOME/.local/bin\"; export PATH=\"$HOME/.local/bin:$PATH\"",
            $"command -v terraform > /dev/null || {{ python3 -c \"import io, urllib.request, zipfile; zipfile.ZipFile(io.BytesIO(urllib.request.urlopen('https://releases.hashicorp.com/terraform/{version}/terraform_{version}_linux_amd64.zip').read())).extract('terraform', '$HOME/.local/bin')\"; chmod +x \"$HOME/.local/bin/terraform\"; }}",
        ]);
    }

    // The azure-cli image has Python, whose zipfile module zips the folder contents without a zip tool.
    private static readonly string Zip = $"python3 -m zipfile -c app.zip {Commands.PackageFolder}/.";

    private static string Slot(EnvironmentSpec environment) => string.IsNullOrWhiteSpace(environment.Slot) ? "" : $" --slot {environment.Slot}";

    public static string Slug(string text) => NotSlug().Replace(text.ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
