using System.Text.RegularExpressions;

namespace CICDPipelineGenerator.Domain;

/// <summary>
/// Checks a spec before anything is written. Every value ends up inside a shell command or a YAML key,
/// so names and paths are limited to characters that cannot break either.
/// </summary>
public static partial class PipelineRules
{
    public static IReadOnlyList<Message> Check(PipelineSpec spec)
    {
        var messages = new List<Message>();
        void Error(string text) => messages.Add(new(Severity.Error, text));
        void Warning(string text) => messages.Add(new(Severity.Warning, text));

        var deploy = spec.Deploy;

        if (!Slug().IsMatch(spec.Name ?? ""))
        {
            Error("The name uses lowercase letters, digits and dashes, starting with a letter (for example my-app).");
        }

        if (spec.Repository is { Length: > 0 } repository && !Repository().IsMatch(repository))
        {
            Error("The repository is written owner/name (GitHub) or group/project (GitLab).");
        }

        if (spec.Platforms.Count == 0)
        {
            Error("Choose at least one pipeline system.");
        }

        if (spec.Environments.Count == 0)
        {
            Error("Add at least one environment.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var environment in spec.Environments)
        {
            var label = string.IsNullOrWhiteSpace(environment.Name) ? "An environment" : $"Environment {environment.Name}";
            if (!Slug().IsMatch(environment.Name ?? ""))
            {
                Error($"{label}: the name uses lowercase letters, digits and dashes, starting with a letter (for example dev or prod).");
            }
            else if (!names.Add(environment.Name!))
            {
                Error($"{label} appears twice.");
            }

            if (spec.Flow == Flow.BranchPerEnvironment && !Branch().IsMatch(environment.Branch ?? ""))
            {
                Error($"{label}: write the branch it deploys from.");
            }

            CheckResource(environment, label, spec, Error);

            if (spec.Platforms.Contains(Platform.AzureDevOps) && spec.NeedsAzure && !Resource().IsMatch(environment.ServiceConnection ?? ""))
            {
                Error($"{label}: write the Azure DevOps service connection it uses.");
            }

            if (environment.Slot is { Length: > 0 } slot && ((deploy.Target is not (Target.AppService or Target.FunctionApp) && !(deploy.Target == Target.AwsElasticBeanstalk && deploy.Strategy == DeployStrategy.BlueGreen)) || !Resource().IsMatch(slot)))
            {
                Error($"{label}: a slot is a name; App Service and Function App use one, and a blue-green Elastic Beanstalk deploy writes its second environment there.");
            }

            CheckServerEnvironment(spec, environment, label, Error);
            CheckSettings(spec, environment, label, Error);

            foreach (var (commands, when) in new[] { (environment.BeforeDeploy, "before"), (environment.AfterDeploy, "after") })
            {
                if (commands.Any(command => command.Contains('\n', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(command)))
                {
                    Error($"{label}: each command {when} the deploy is one line.");
                }
            }

            if (environment.ParametersFile is { Length: > 0 } parameters && (!(spec.IsInfrastructure || spec.UsesHelm || spec.Deploy.Target == Target.GitOps) || !PathPattern().IsMatch(parameters)))
            {
                Error($"{label}: a parameters file is a path without spaces, and only Terraform and Bicep use one.");
            }
        }

        if (spec.Flow == Flow.Tag && !TagPatternRule().IsMatch(spec.TagPattern ?? ""))
        {
            Error("The tags that deploy are a name or a prefix followed by * (for example v*).");
        }

        if (spec.Flow == Flow.Tag && spec.NeedsAzure && spec.Platforms.Contains(Platform.GitLab))
        {
            Warning("GitLab signs in to Azure per tag: the federated credential needs a flexible subject that matches the tags (see SETUP.md).");
        }

        if (spec.Promotes && !Branch().IsMatch(spec.MainBranch ?? ""))
        {
            Error("Write the branch that deploys.");
        }

        if (spec.Flow == Flow.BranchPerEnvironment && spec.Environments.Count > 1
            && spec.Environments.Select(environment => environment.Branch).Distinct(StringComparer.Ordinal).Count() < spec.Environments.Count)
        {
            Warning("Two environments deploy from the same branch, so one push changes both at once.");
        }

        foreach (var path in spec.PathFilters.Where(path => !PathPattern().IsMatch(path) && !Glob().IsMatch(path)))
        {
            Error($"The path filter \"{path}\" is a path or pattern without spaces (for example src/** ).");
        }

        if (spec.Schedule is { Length: > 0 } schedule && !Cron().IsMatch(schedule))
        {
            Error("The schedule is a cron expression with five fields, such as 0 6 * * 1-5 (06:00 UTC, Monday to Friday).");
        }

        CheckBuild(spec, Error, Warning);
        CheckFeeds(spec, Error, Warning);
        CheckDeploy(spec, Error);
        CheckServers(spec, Error);
        CheckAgent(spec, Error);
        CheckCloud(spec, Error);
        CheckPackage(spec, Error, Warning);
        CheckDatabase(spec, Error, Warning);

        if (spec.Environments.Count > 0 && !spec.Environments[^1].RequireApproval)
        {
            Warning($"The last environment ({spec.Environments[^1].Name}) changes with no approval.");
        }

        var build = spec.Build;
        if (build.Services.Count > 0)
        {
            if (spec.IsInfrastructure)
            {
                Warning("Services for the tests: infrastructure code has no tests that use them, so none is started.");
            }
            else if (spec.IsWindowsBuild || spec.IsMacBuild)
            {
                Error("Services for the tests are Linux containers: use a Linux build agent, or take the services out.");
            }
            else if (build.Agent == BuildAgent.SelfHostedLinux || spec.Platforms.Contains(Platform.Jenkins))
            {
                Warning("Services for the tests: your own agents need Docker to start them.");
            }
        }

        if (build.CodeQl)
        {
            if (Quality.CodeQl(spec) is null)
            {
                Warning("CodeQL reads C#, JavaScript and TypeScript, Python, Java and Kotlin, Ruby, Rust, Go and C or C++: no analysis is written for this build.");
            }
            else if (spec.Platforms.Any(platform => platform != Platform.GitHubActions))
            {
                Warning("CodeQL is written for GitHub Actions only; the other systems can analyse the code with Semgrep.");
            }
        }

        if (build.AlsoTestWith.Count > 0)
        {
            if (spec.IsInfrastructure || build.Stack == Stack.None || build.CustomCommands.Count > 0)
            {
                Warning("Also test with: there is no language version to change here (infrastructure, no build, or your own commands), so no other job is written.");
            }
            else if (build.AlsoTestWith.Any(version => !VersionNumber().IsMatch(version.Trim())))
            {
                Error("Also test with: write versions like 20 or 3.12, one per line.");
            }
            else if (build.AlsoTestWith.Select(version => version.Trim()).Distinct().Count() > 5 || build.AlsoTestWith.Any(version => version.Trim() == build.EffectiveVersion))
            {
                Error("Also test with: at most five versions, and none equal to the version of the build.");
            }
        }

        if (spec.Deploy.Target == Target.AwsEcs && spec.Deploy.Strategy == DeployStrategy.Canary && (spec.Deploy.CanaryPercent != 10 || spec.Deploy.CanaryMinutes is not (5 or 15)))
        {
            Warning("Canary on ECS uses CodeDeploy's own steps: a tenth of the traffic first, for 5 or 15 minutes.");
        }

        if (!string.IsNullOrWhiteSpace(spec.TemplateRepository))
        {
            if (!Repository().IsMatch(spec.TemplateRepository) || !Branch().IsMatch(spec.TemplateRef ?? ""))
            {
                Error("Shared templates: write the repository that keeps them as owner/name (GitHub), group/project (GitLab) or Project/Repository (Azure Repos), and the branch or tag to use.");
            }
            else if (spec.SeveralApps)
            {
                Warning("Shared templates are for a repository with one application: the pipelines are written whole.");
            }
            else if (spec.Platforms.Any(platform => !SharedTemplates.Covers(platform)))
            {
                Warning("Shared templates are written for GitHub Actions, Azure DevOps and GitLab; the other systems get their whole pipeline.");
            }
        }

        if (spec.Deploy.PreviewPullRequests)
        {
            var container = spec.Deploy.Target == Target.ContainerApp;
            if (!spec.BuildPullRequests || (spec.Deploy.Target != Target.AppService && !container))
            {
                Warning("A preview per pull request is written for App Service and Container Apps, with pull requests built (Static Web Apps makes its own): none is written here.");
            }
            else if (container && spec.Deploy.Strategy == DeployStrategy.Standard)
            {
                Warning("A preview per pull request on Container Apps needs blue-green or canary: with the app's own update, a new revision would take the traffic of production. None is written.");
            }
            else if (container ? spec.Platforms.Any(platform => platform != Platform.GitHubActions) : spec.Platforms.Any(platform => platform is not (Platform.GitHubActions or Platform.GitLab)))
            {
                Warning(container
                    ? "A preview per pull request on Container Apps is written for GitHub Actions; the other systems build and test the pull request."
                    : "A preview per pull request is written for GitHub Actions and GitLab: the other systems have nothing that runs when a pull request closes, so they build and test it.");
            }
        }

        var more = spec.Platforms.Where(MoreSystems.Is).Select(Systems.Name).ToList();
        if (more.Count > 0)
        {
            var systems = string.Join(", ", more);
            if (spec.IsWindowsBuild || spec.IsMacBuild || spec.DeploysOnMac || spec.OnServerRunner || (spec.IsRemote && spec.UsesPowerShell))
            {
                Error($"{systems}: their jobs are written for Linux images, so Windows and macOS builds, deploys on the servers' own runners and WinRM are not written for them (SSH to Linux servers is).");
            }

            if (spec.SeveralApps)
            {
                Error($"{systems}: several applications that deploy separately are not written for them.");
            }

            var set = spec.Platforms.Where(platform => platform is Platform.TravisCi or Platform.CloudBuild or Platform.CodePipeline).Select(Systems.Name).ToList();
            if (set.Count > 0 && (spec.PathFilters.Count > 0 || !string.IsNullOrWhiteSpace(spec.Schedule)))
            {
                Warning($"{string.Join(", ", set)}: path filters and the schedule are set in the system (its triggers), not in the file; SETUP.md says where.");
            }

            if (spec.Platforms.Contains(Platform.TeamCity) && !string.IsNullOrWhiteSpace(spec.Schedule))
            {
                Warning("TeamCity: add the schedule as a Schedule trigger of the build configuration; its cron has its own fields.");
            }

            if (spec.Platforms.Contains(Platform.ArgoWorkflows) && spec.PathFilters.Count > 0)
            {
                Warning("Argo Workflows: what starts a workflow on a push (Argo Events) also filters the paths.");
            }

            if (spec.Deploy.RollbackPipeline && spec.IsServerApp && spec.Platforms.Contains(Platform.TravisCi))
            {
                Warning("Travis CI has no build started by hand with a choice: its rollback is running the build of an earlier commit again.");
            }

            if (spec.Environments.Any(environment => environment.RequireApproval) && spec.Platforms.Any(platform => platform is Platform.TravisCi or Platform.CloudBuild))
            {
                Warning("Travis CI and Cloud Build have no approval inside a build: an environment that needs one does not wait there.");
            }
        }

        if (spec.IsServerApp && spec.OnServerRunner && spec.Deploy.Strategy == DeployStrategy.Canary && spec.Platforms.Contains(Platform.AzureDevOps))
        {
            Warning("Azure DevOps updates the environment's machines one after another by itself: the wait after the first server is not written there.");
        }

        if (spec.CreateRelease)
        {
            if (spec.Flow != Flow.Tag)
            {
                Error("A release is published for a tag: choose \"a tag deploys\".");
            }
            else if (spec.Platforms.Any(platform => platform is not (Platform.GitHubActions or Platform.GitLab)))
            {
                Warning("Releases are written for GitHub Actions and GitLab CI/CD; the other systems have no release of their own.");
            }
        }

        if (spec.SeveralApps && spec.Platforms.Any(platform => platform is Platform.Bitbucket or Platform.CircleCi))
        {
            Error("Bitbucket Pipelines and CircleCI read one file per repository: several applications that deploy separately are not written for them.");
        }

        if (spec.Platforms.Contains(Platform.Bitbucket) && (spec.Build.Agent == BuildAgent.HostedWindows || (spec.IsRemote && spec.UsesPowerShell)))
        {
            Error("Bitbucket Pipelines has no hosted Windows: build on your own Windows runner, and deploy with the runner installed on the servers.");
        }

        if (spec.Platforms.Contains(Platform.CircleCi) && spec.PathFilters.Count > 0 && !CircleCiGenerator.FiltersPaths(spec))
        {
            Warning("CircleCI filters paths with a first configuration that continues with the pipeline; with tags, a schedule or a rollback pipeline that is not written, and every push runs.");
        }

        if (spec.Platforms.Contains(Platform.GitHubActions) && string.IsNullOrWhiteSpace(spec.Repository))
        {
            Warning("Without the repository, the setup guide cannot write the exact federated credential subjects.");
        }

        return messages;
    }

    private static void CheckBuild(PipelineSpec spec, Action<string> error, Action<string> warning)
    {
        var build = spec.Build;
        if (spec.IsInfrastructure)
        {
            if (build.Stack != Stack.None || build.CustomCommands.Count > 0)
            {
                warning("Infrastructure targets deploy infrastructure only: the build settings are not used.");
            }

            return;
        }

        if (!PathPattern().IsMatch(build.WorkingDirectory ?? ""))
        {
            error("The working folder is a path without spaces (. is the repository root).");
        }

        if (build.Version is { Length: > 0 } version && !VersionPattern().IsMatch(version.Trim()) && !(build.Stack is Stack.Rust or Stack.Flutter && version.Trim() is "stable" or "beta" or "nightly"))
        {
            error("The version is a number such as 10.0, 22 or 3.12.");
        }

        if (build.CustomCommands.Count > 0)
        {
            if (build.CustomCommands.Any(command => command.Contains('\n') || string.IsNullOrWhiteSpace(command)))
            {
                error("Each custom command is one line.");
            }

            return;
        }

        if (build.PublishTestResults && ((build.Stack != Stack.DotNet && !build.WritesJUnit) || !build.RunTests))
        {
            error("Test results are kept for .NET, Java (Maven, Gradle, Android), Scala, Go, PHP, Swift, C or C++ and, on Linux or macOS agents, Node.js, Python, Ruby, Rust and Flutter builds that run the tests.");
        }

        if (build.AuditDependencies && build.Stack is not (Stack.DotNet or Stack.Node or Stack.Python or Stack.Go or Stack.Ruby or Stack.Php or Stack.Rust))
        {
            error("Checking the packages for known vulnerabilities is for .NET, Node.js, Python, Go, Ruby, PHP and Rust builds.");
        }

        if (build.CachePackages && !PackageCache.Applies(spec))
        {
            error("Keeping the packages between runs is for .NET, Node.js, Python, Java (Maven, Gradle), Scala, Go, Rust, PHP and Flutter builds on hosted Linux agents.");
        }

        switch (build.Stack)
        {
            case Stack.DotNetFramework when !PathPattern().IsMatch(build.ProjectPath ?? ""):
                error(".NET Framework: write the project to publish (for example src/Web/Web.csproj).");
                break;
            case Stack.DotNetFramework when build.TestPath is { Length: > 0 } solution && !PathPattern().IsMatch(solution):
                error(".NET Framework: the solution is a path without spaces.");
                break;
            case Stack.DotNet when spec.UsesPackage && !PathPattern().IsMatch(build.ProjectPath ?? ""):
                error(".NET: write the project to publish (for example src/Api/Api.csproj).");
                break;
            case Stack.DotNet when !spec.UsesPackage && !PathPattern().IsMatch(string.IsNullOrWhiteSpace(build.TestPath) ? build.ProjectPath ?? "" : build.TestPath):
                error(".NET: write the solution or project to build and test.");
                break;
            case Stack.DotNet when build.TestPath is { Length: > 0 } test && !PathPattern().IsMatch(test):
                error(".NET: the solution or test project is a path without spaces.");
                break;
            case Stack.Node when !PathPattern().IsMatch(build.OutputFolder ?? ""):
                error("Node: write the folder the build creates (for example dist).");
                break;
            case Stack.None when spec.UsesPackage:
                error("Choose what the code is written in, or write the build commands: there is nothing to deploy.");
                break;
        }

        if (spec.Deploy.Target == Target.StaticWebApp && build.Stack is not (Stack.Node or Stack.None))
        {
            warning("Static Web Apps serve files: the out/app folder must hold the site (for example a Blazor WebAssembly wwwroot).");
        }
    }

    private static void CheckFeeds(PipelineSpec spec, Action<string> error, Action<string> warning)
    {
        var feeds = spec.Build.PackageFeeds;
        if (feeds.Count == 0)
        {
            return;
        }

        if (spec.IsInfrastructure)
        {
            error("Package feeds are for builds: Terraform and Bicep do not restore packages.");
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in feeds)
        {
            var label = string.IsNullOrWhiteSpace(feed.Name) ? "A package feed" : $"Package feed {feed.Name}";
            if (!FeedName().IsMatch(feed.Name ?? ""))
            {
                error($"{label}: the name uses letters, digits and underscores, starting with a letter (for example company).");
            }
            else if (!names.Add(feed.Name!))
            {
                error($"{label} appears twice.");
            }

            if (feed.Type == FeedType.NuGet && spec.Build.Stack is not (Stack.DotNet or Stack.DotNetFramework))
            {
                error($"{label}: NuGet feeds are for .NET builds.");
            }

            if (feed.Type == FeedType.Npm && spec.Build.Stack != Stack.Node)
            {
                error($"{label}: npm registries are for Node.js builds.");
            }

            if (feed.Type == FeedType.Maven && spec.Build.Stack is not (Stack.Java or Stack.Gradle or Stack.Android or Stack.Scala))
            {
                error($"{label}: Maven repositories are for Java builds (Maven, Gradle, Android) and Scala.");
            }

            var (language, stack) = feed.Type switch
            {
                FeedType.Go => ("Go module proxies", Stack.Go),
                FeedType.RubyGems => ("RubyGems sources", Stack.Ruby),
                FeedType.Composer => ("Composer repositories", Stack.Php),
                FeedType.Cargo => ("Cargo registries", Stack.Rust),
                FeedType.Hex => ("Hex repositories", Stack.Elixir),
                FeedType.Pub => ("pub repositories", Stack.Flutter),
                FeedType.SwiftRegistry => ("Swift package registries", Stack.Swift),
                FeedType.Conan => ("Conan remotes", Stack.Cpp),
                _ => ("", spec.Build.Stack),
            };
            if (spec.Build.Stack != stack)
            {
                error($"{label}: {language} are for {stack} builds.");
            }
            else if (language.Length > 0 && (feed.Kind == FeedKind.AzureArtifacts || (feed.Kind == FeedKind.GitHubPackages && feed.Type != FeedType.RubyGems)))
            {
                error($"{label}: {language} are written for JFrog Artifactory or another feed by its URL{(feed.Type == FeedType.RubyGems ? " (and GitHub Packages)" : "")}.");
            }

            if (feed.Type == FeedType.Pip && spec.Build.Stack != Stack.Python)
            {
                error($"{label}: Python package indexes are for Python builds.");
            }

            if (feed.Kind == FeedKind.GitHubPackages && feed.Type == FeedType.Pip)
            {
                error($"{label}: GitHub Packages has no Python package index.");
            }

            if (feed.Kind == FeedKind.GitHubPackages && feed.Type == FeedType.Maven && !Resource().IsMatch(feed.Feed ?? ""))
            {
                error($"{label}: for Maven on GitHub Packages write the repository the packages belong to.");
            }

            switch (feed.Kind)
            {
                case FeedKind.AzureArtifacts:
                    if (!Resource().IsMatch(feed.Organization ?? "") || !Resource().IsMatch(feed.Feed ?? ""))
                    {
                        error($"{label}: write the Azure DevOps organization and the feed name.");
                    }

                    if (feed.Project is { Length: > 0 } project && !Resource().IsMatch(project))
                    {
                        error($"{label}: the project is a name without spaces (leave it empty for an organization-scoped feed).");
                    }

                    break;
                case FeedKind.GitHubPackages:
                    if (!Resource().IsMatch(feed.Organization ?? ""))
                    {
                        error($"{label}: write the GitHub owner (user or organization) of the packages.");
                    }
                    else if (feed.Type == FeedType.Npm && !string.Equals(feed.Scope, "@" + feed.Organization!.ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        error($"{label}: GitHub Packages npm packages use the scope @{feed.Organization!.ToLowerInvariant()}.");
                    }

                    break;
                case FeedKind.JFrog:
                    if (!FeedUrl().IsMatch(feed.Url ?? "") || !Resource().IsMatch(feed.Feed ?? ""))
                    {
                        error($"{label}: write the JFrog address (https://company.jfrog.io) and the repository key.");
                    }

                    if (feed.JFrogOidcProvider is { Length: > 0 } provider && !Resource().IsMatch(provider))
                    {
                        error($"{label}: the JFrog OpenID Connect integration is a name without spaces.");
                    }

                    break;
                default:
                    if (!FeedUrl().IsMatch(feed.Url ?? ""))
                    {
                        error($"{label}: write the feed URL (https://..., without spaces or quotes).");
                    }

                    break;
            }

            if (feed.Scope is { Length: > 0 } scope && (feed.Type != FeedType.Npm || !NpmScope().IsMatch(scope)))
            {
                error($"{label}: a scope is written @name in lowercase, and only npm registries use one.");
            }
        }

        if (spec.IsAzureContainer)
        {
            warning("The image build gets each feed's user and token as secret build arguments (FEED_<NAME>_USER, FEED_<NAME>_TOKEN): declare the ARGs in the Dockerfile to use them.");
        }
    }

    private static void CheckDeploy(PipelineSpec spec, Action<string> error)
    {
        var deploy = spec.Deploy;
        if (deploy.Os == HostOs.Windows && deploy.Target is not (Target.AppService or Target.FunctionApp))
        {
            error("Windows is only for App Service and Function App.");
        }

        if (spec.IsAzureContainer)
        {
            if (!RegistryPattern().IsMatch(deploy.Registry ?? ""))
            {
                error("Write the Azure Container Registry name (letters and digits, without .azurecr.io).");
            }

            if (!ImagePattern().IsMatch(deploy.ImageName ?? ""))
            {
                error("Write the image name in lowercase (for example my-app or team/my-app).");
            }

            if (!PathPattern().IsMatch(deploy.Dockerfile ?? "") || !PathPattern().IsMatch(deploy.DockerContext ?? ""))
            {
                error("The Dockerfile and its folder are paths without spaces.");
            }
        }

        if (deploy.Target == Target.Aks && !spec.UsesHelm)
        {
            if (!Kubernetes().IsMatch(deploy.Namespace ?? "") || !Kubernetes().IsMatch(deploy.DeploymentName ?? "") || !Kubernetes().IsMatch(deploy.ContainerName ?? ""))
            {
                error("AKS: write the namespace, the deployment and the container name (lowercase letters, digits and dashes).");
            }

            if (!PathPattern().IsMatch(deploy.Manifests ?? ""))
            {
                error("AKS: write the folder or file with the Kubernetes manifests.");
            }
        }

        if (!string.IsNullOrWhiteSpace(deploy.HelmChart))
        {
            if (!spec.IsKubernetes)
            {
                error("A Helm chart is for Kubernetes targets (AKS, GKE, EKS or your own cluster).");
            }
            else if (!PathPattern().IsMatch(deploy.HelmChart) || !Kubernetes().IsMatch(deploy.Namespace ?? "") || !Kubernetes().IsMatch(deploy.DeploymentName ?? ""))
            {
                error("Helm: write the chart's folder (for example charts/my-app), the namespace and the release name (in \"Deployment\").");
            }
        }

        if (spec.IsGenericRegistry)
        {
            var registry = deploy.RegistryKind == RegistryKind.Other ? RegistryAddress() : ImagePattern();
            if (!registry.IsMatch(deploy.Registry ?? ""))
            {
                error(deploy.RegistryKind == RegistryKind.Other
                    ? "Write the registry's address, with its path when it has one (for example registry.contoso.com/team)."
                    : "Write the user or organisation the image belongs to in the registry, in lowercase.");
            }

            if (!ImagePattern().IsMatch(deploy.ImageName ?? "") || !PathPattern().IsMatch(deploy.Dockerfile ?? "") || !PathPattern().IsMatch(deploy.DockerContext ?? ""))
            {
                error("Write the image name in lowercase (for example my-app); the Dockerfile and its folder are paths without spaces.");
            }
        }

        if (deploy.Target is Target.Kubernetes or Target.AwsEks && !spec.UsesHelm
            && (!Kubernetes().IsMatch(deploy.Namespace ?? "") || !Kubernetes().IsMatch(deploy.DeploymentName ?? "") || !Kubernetes().IsMatch(deploy.ContainerName ?? "") || !PathPattern().IsMatch(deploy.Manifests ?? "")))
        {
            error("Kubernetes: write the namespace, the deployment, the container name and the folder or file with the manifests.");
        }

        if (deploy.Target == Target.Terraform)
        {
            if (!PathPattern().IsMatch(deploy.InfraDirectory ?? ""))
            {
                error("Terraform: write the folder with the .tf files.");
            }

            if (!FullVersion().IsMatch(deploy.TerraformVersion ?? ""))
            {
                error("Terraform: the version is a full release such as 1.9.8.");
            }

            if (!spec.NeedsAzure)
            {
                if (!Kubernetes().IsMatch(deploy.Bucket ?? ""))
                {
                    error("Terraform: write the bucket that keeps the state (S3 on AWS, Cloud Storage on Google Cloud).");
                }
            }
            else if (!Resource().IsMatch(deploy.StateResourceGroup ?? "") || !StoragePattern().IsMatch(deploy.StateStorageAccount ?? "")
                || !Kubernetes().IsMatch(deploy.StateContainer ?? ""))
            {
                error("Terraform: write where the state is kept (resource group, storage account in lowercase letters and digits, container).");
            }
        }

        if (deploy.Target is Target.Bicep or Target.ArmTemplate or Target.CloudFormation && !PathPattern().IsMatch(deploy.BicepTemplate ?? ""))
        {
            error("Write the template file (for example infra/main.bicep, infra/azuredeploy.json or infra/template.yaml).");
        }

        if (spec.IsTokenHost)
        {
            foreach (var environment in spec.Environments.Where(environment => !Resource().IsMatch(environment.ResourceName ?? "")))
            {
                error($"Environment {environment.Name}: write its {(deploy.Target == Target.Netlify ? "Netlify site (ID or name)" : deploy.Target == Target.Vercel ? "Vercel project ID" : deploy.Target == Target.DigitalOcean ? "DigitalOcean app ID" : deploy.Target == Target.Heroku ? "Heroku app" : deploy.Target == Target.ServiceFabric ? "Service Fabric application name" : "Cloudflare Pages project")}.");
            }
        }

        if (deploy.Target == Target.ServiceFabric && spec.Build.CustomCommands.Count == 0)
        {
            if (!PathPattern().IsMatch(deploy.FabricProject ?? "") || !deploy.FabricProject!.EndsWith(".sfproj", StringComparison.OrdinalIgnoreCase))
            {
                error("Service Fabric: write the application project the build packages (for example src/Orders/Orders.sfproj).");
            }

            if (!spec.IsWindowsBuild || spec.Build.Stack is not (Stack.DotNet or Stack.DotNetFramework))
            {
                error("Service Fabric: the application is packaged with MSBuild, so the build is .NET or .NET Framework on a Windows build agent.");
            }
        }

        if (deploy.Target == Target.ServiceFabric && !AppIdentifier().IsMatch(deploy.AppId ?? ""))
        {
            error("Service Fabric: write the application type name of the package (ApplicationTypeName in ApplicationManifest.xml) as the app identifier.");
        }

        if (deploy.Target == Target.GitOps)
        {
            if (!GitRepository().IsMatch(deploy.GitOpsRepository ?? ""))
            {
                error("GitOps: write the manifests repository without https:// (for example github.com/contoso/deployments).");
            }

            foreach (var environment in spec.Environments.Where(environment => !PathPattern().IsMatch(environment.ParametersFile ?? "")))
            {
                error($"Environment {environment.Name}: write the file of the manifests repository that names the image (for example apps/shop/prod/deployment.yaml).");
            }
        }

        if (deploy.Target == Target.Database && deploy.Migrations == MigrationKind.None)
        {
            error("Only the database: choose how it is brought up to date (an Entity Framework Core bundle, SQL scripts or a dacpac).");
        }

        if (deploy.Target is Target.ApiManagement or Target.DataFactory && !PathPattern().IsMatch(deploy.BicepTemplate ?? ""))
        {
            error(deploy.Target == Target.ApiManagement ? "API Management: write the OpenAPI file of the repository (for example api/openapi.json)." : "Data Factory: write the ARM template the factory publishes (for example adf/ARMTemplateForFactory.json).");
        }

        if (deploy.Target == Target.ApiManagement && !AppIdentifier().IsMatch(deploy.AppId ?? ""))
        {
            error("API Management: write the API's identifier (it is also its path).");
        }

        if (deploy.Target == Target.LogicApp && !PathPattern().IsMatch(deploy.InfraDirectory ?? ""))
        {
            error("Logic Apps: write the folder with the workflows (the one with host.json).");
        }

        if (deploy.SubmitForReview && deploy.Target != Target.AppStore)
        {
            error("Sending the build to review is for the TestFlight target.");
        }

        foreach (var environment in spec.Environments.Where(Commands.TestsAfter))
        {
            if (spec.UsesPowerShell || (environment.EndToEndFolder is { Length: > 0 } folder && !PathPattern().IsMatch(folder)) || (environment.LoadTestScript is { Length: > 0 } script && !PathPattern().IsMatch(script)))
            {
                error($"Environment {environment.Name}: the end-to-end tests are a folder and the load test a script of the repository, and they run in bash (not on Windows servers).");
            }
        }

        if (deploy.Target == Target.AwsSam && !PathPattern().IsMatch(deploy.InfraDirectory ?? ""))
        {
            error("SAM: write the folder with template.yaml.");
        }

        foreach (var environment in spec.Environments.Where(environment => !string.IsNullOrWhiteSpace(environment.DeployWindow)))
        {
            var match = DeployWindowPattern().Match(environment.DeployWindow!);
            if (!match.Success || string.CompareOrdinal(match.Groups[1].Value, match.Groups[2].Value) > 0 || string.CompareOrdinal(match.Groups[3].Value, match.Groups[4].Value) >= 0)
            {
                error($"Environment {environment.Name}: the deploy window is days and hours in UTC, as 1-5 08-18 (Monday to Friday, 08:00 to 18:00).");
            }
        }

        if (deploy.Strategy != DeployStrategy.Standard)
        {
            if (!Strategies.Supports(spec))
            {
                error("Blue-green and canary deploys are for App Service and Functions (with a slot), Container Apps, Kubernetes, Cloud Run, Lambda and ECS (with CodeDeploy); Elastic Beanstalk does blue-green (with a second environment) and your own servers canary (the first server before the others).");
            }
            else if (deploy.Target == Target.AwsElasticBeanstalk && spec.Environments.All(environment => string.IsNullOrWhiteSpace(environment.Slot)))
            {
                error("Blue-green on Elastic Beanstalk swaps two environments: write the second one as the slot of the environments that should deploy that way.");
            }
            else if (spec.IsServerApp)
            {
                if (spec.Environments.All(environment => Strategies.FirstServer(spec, environment) is null))
                {
                    error("Canary on your servers updates the first server before the others: an environment needs several servers.");
                }
            }
            else if (deploy.Target is Target.AppService or Target.FunctionApp && spec.Environments.All(environment => string.IsNullOrWhiteSpace(environment.Slot)))
            {
                error("Blue-green and canary on App Service and Functions use a deployment slot: write the slot of the environments that should deploy that way (for example staging).");
            }

            if (deploy.Strategy == DeployStrategy.Canary && (deploy.CanaryPercent is < 1 or > 99 || deploy.CanaryMinutes is < 1 or > 120))
            {
                error("Canary: the share is between 1 and 99 percent and the wait between 1 and 120 minutes.");
            }
        }

        if (deploy.Target is Target.AwsCdk or Target.Pulumi)
        {
            if (!PathPattern().IsMatch(deploy.InfraDirectory ?? ""))
            {
                error(deploy.Target == Target.AwsCdk ? "CDK: write the folder with cdk.json." : "Pulumi: write the folder with Pulumi.yaml.");
            }

            foreach (var environment in spec.Environments.Where(environment => !PathPattern().IsMatch(environment.ResourceName ?? "")))
            {
                error($"Environment {environment.Name}: write the {(deploy.Target == Target.AwsCdk ? "CDK stack" : "Pulumi stack (for example dev or my-org/dev)")}.");
            }
        }
    }

    private static void CheckResource(EnvironmentSpec environment, string label, PipelineSpec spec, Action<string> error)
    {
        var target = spec.Deploy.Target;
        if (spec.IsCloudCli || target is Target.Pulumi)
        {
            return;
        }

        var windows = target is Target.WindowsService or Target.Iis or Target.ScheduledTask;
        var needsGroup = target is not (Target.LinuxService or Target.Package or Target.Terraform or Target.StaticWebApp or Target.WindowsService or Target.Iis or Target.ScheduledTask or Target.Script);
        var needsName = target is not (Target.Terraform or Target.Bicep or Target.ArmTemplate or Target.Script or Target.Package);
        if (needsGroup && !Resource().IsMatch(environment.ResourceGroup ?? ""))
        {
            error($"{label}: write the resource group.");
        }

        if (target == Target.LinuxService)
        {
            if (!UnitName().IsMatch(environment.ResourceName ?? ""))
            {
                error($"{label}: write the systemd service name (for example billing or billing.service).");
            }
        }
        else if (needsName && !(windows ? WindowsName() : Resource()).IsMatch(environment.ResourceName ?? ""))
        {
            error($"{label}: write the {ResourceLabel(target)}{(windows ? " (letters, digits, spaces, dots, dashes; no quotes)" : "")}.");
        }
    }

    private static void CheckServerEnvironment(PipelineSpec spec, EnvironmentSpec environment, string label, Action<string> error)
    {
        var windows = spec.Deploy.Target is Target.WindowsService or Target.Iis or Target.ScheduledTask;
        if (spec.IsRemote && (environment.Hosts.Count == 0 || environment.Hosts.Any(host => !HostName().IsMatch(host ?? ""))))
        {
            error($"{label}: write the addresses of its servers, one per line (for example app1.contoso.com).");
        }

        if (!spec.IsRemote && environment.Hosts.Count > 0)
        {
            error($"{label}: server addresses are only for remote deploys (SSH or WinRM).");
        }

        if (spec.OnServerRunner && !Label().IsMatch(environment.ServerLabel ?? ""))
        {
            error($"{label}: write the label (GitHub runner label, Azure DevOps VM tag, GitLab runner tag) of its servers.");
        }

        var linux = spec.Deploy.Target == Target.LinuxService;
        if (linux)
        {
            if (environment.TargetPath is { Length: > 0 } folder && !LinuxPath().IsMatch(folder))
            {
                error($"{label}: the folder is a full path such as /opt/billing.");
            }

            if (environment.HealthCheckUrl is { Length: > 0 } address && !HealthUrl().IsMatch(address))
            {
                error($"{label}: the health check is an http(s) address without spaces or quotes.");
            }
        }
        else if (environment.TargetPath is { Length: > 0 } path && (!windows || !WindowsPath().IsMatch(path)))
        {
            error($"{label}: the folder is a full Windows path such as D:\\Apps\\Billing, and only Windows services and IIS use one.");
        }

        if (environment.AppPool is { Length: > 0 } pool && (spec.Deploy.Target != Target.Iis || !WindowsName().IsMatch(pool)))
        {
            error($"{label}: the application pool is a name without quotes, and only IIS uses one.");
        }

        if (!linux && environment.HealthCheckUrl is { Length: > 0 } url && ((!windows && spec.Deploy.Strategy == DeployStrategy.Standard) || !HealthUrl().IsMatch(url)))
        {
            error($"{label}: the health check is an http(s) address without spaces or quotes; servers and blue-green or canary deploys use one.");
        }

        if (spec.Deploy.Target == Target.WindowsService && !string.IsNullOrWhiteSpace(spec.Deploy.ServiceExecutable) && string.IsNullOrWhiteSpace(environment.TargetPath))
        {
            error($"{label}: to create the service when it is missing, write the folder it is installed in.");
        }
    }

    private static void CheckCloud(PipelineSpec spec, Action<string> error)
    {
        if (!spec.IsAws && !spec.IsGcp)
        {
            return;
        }

        var deploy = spec.Deploy;
        if (!Kubernetes().IsMatch(deploy.CloudRegion ?? "") && !(deploy.Target is Target.Pulumi or Target.Terraform or Target.FirebaseHosting && spec.IsGcp))
        {
            error(spec.IsAws ? "Write the AWS region (for example us-east-1)." : "Write the Google Cloud region (for example us-central1).");
        }

        if (spec.IsContainer)
        {
            if (!ImagePattern().IsMatch(deploy.ImageName ?? "") || !PathPattern().IsMatch(deploy.Dockerfile ?? "") || !PathPattern().IsMatch(deploy.DockerContext ?? ""))
            {
                error(spec.IsAws ? "Write the ECR repository in lowercase (for example my-app); the Dockerfile and its folder are paths without spaces." : "Write the image name in lowercase; the build folder is a path without spaces.");
            }

            if (spec.IsGcp && !Kubernetes().IsMatch(deploy.Registry ?? ""))
            {
                error("Write the Artifact Registry repository (lowercase letters, digits and dashes).");
            }
        }

        switch (deploy.Target)
        {
            case Target.AwsElasticBeanstalk when !Kubernetes().IsMatch(deploy.Bucket ?? ""):
                error("Elastic Beanstalk: write the S3 bucket that keeps the application versions.");
                break;
            case Target.AwsEcs when !Resource().IsMatch(deploy.ContainerName ?? ""):
                error("ECS: write the name of the container in the task definition.");
                break;
            case Target.GcpCloudFunctions when !FunctionRuntime().IsMatch(deploy.FunctionRuntime ?? "") || !EntryPoint().IsMatch(deploy.FunctionEntryPoint ?? ""):
                error("Cloud Functions: write the runtime (dotnet8, nodejs22, python312, java21) and the entry point.");
                break;
            case Target.GcpAppEngine when !PathPattern().IsMatch(deploy.AppYaml ?? ""):
                error("App Engine: write the app.yaml to deploy.");
                break;
            case Target.GcpGke when !spec.UsesHelm && (!Kubernetes().IsMatch(deploy.Namespace ?? "") || !Kubernetes().IsMatch(deploy.DeploymentName ?? "") || !Kubernetes().IsMatch(deploy.ContainerName ?? "") || !PathPattern().IsMatch(deploy.Manifests ?? "")):
                error("GKE: write the namespace, the deployment, the container name and the folder or file with the manifests.");
                break;
        }

        foreach (var environment in spec.Environments)
        {
            var label = $"Environment {environment.Name}";
            var (group, name) = CloudLabels(deploy.Target);
            if (group is not null && !Resource().IsMatch(environment.ResourceGroup ?? ""))
            {
                error($"{label}: write the {group}.");
            }

            if (name is not null && !Resource().IsMatch(environment.ResourceName ?? ""))
            {
                error($"{label}: write the {name}.");
            }
        }
    }

    /// <summary>What the two names of an environment mean on each cloud target (null: not used).</summary>
    public static (string? Group, string? Name) CloudLabels(Target target) => target switch
    {
        Target.AwsS3 => (null, "S3 bucket"),
        Target.AwsLambda => (null, "Lambda function"),
        Target.AwsElasticBeanstalk => ("Elastic Beanstalk application", "Elastic Beanstalk environment"),
        Target.AwsEcs => ("ECS cluster", "ECS service"),
        Target.AwsEks => (null, "EKS cluster"),
        Target.CloudFormation => (null, "CloudFormation stack"),
        Target.AwsSam => (null, "CloudFormation stack of the application"),
        Target.AwsAppRunner => (null, "App Runner service"),
        Target.FirebaseHosting => (null, "Firebase project"),
        Target.GcpCloudRun => (null, "Cloud Run service"),
        Target.GcpCloudFunctions => (null, "function"),
        Target.GcpGke => (null, "GKE cluster"),
        _ => (null, null),
    };

    private static void CheckPackage(PipelineSpec spec, Action<string> error, Action<string> warning)
    {
        var deploy = spec.Deploy;
        if (deploy.RollbackPipeline && !spec.IsServerApp)
        {
            error("A rollback pipeline is for applications on your servers (Windows service, IIS, scheduled task, Linux service), which keep their previous version there.");
        }

        if (deploy.Remote && !spec.IsServerApp)
        {
            error("A remote deploy (SSH or WinRM) is for applications on your servers.");
        }

        if (deploy.Target == Target.LinuxService && deploy.SettingsFile.EndsWith(".config", StringComparison.OrdinalIgnoreCase))
        {
            error("On Linux the settings file is JSON (appsettings.json).");
        }

        var build = spec.Build;
        if (build.Coverage && (build.Stack != Stack.DotNet || !build.RunTests || build.CustomCommands.Count > 0))
        {
            error("Code coverage is collected for .NET builds that run the tests.");
        }

        if (build.SonarProjectKey is { Length: > 0 } key
            && (!SonarKey().IsMatch(key) || build.Stack is not (Stack.DotNet or Stack.Java or Stack.Gradle) || build.CustomCommands.Count > 0 || spec.IsWindowsBuild))
        {
            error("SonarQube analyses .NET and Java (Maven or Gradle) builds on Linux agents; the project key uses letters, digits, dots, dashes, underscores and colons.");
        }

        if (spec.SeveralApps && spec.PathFilters.Count == 0)
        {
            warning("With several applications in the repository, write the paths of this one in \"Only when these paths change\": without them every push runs its pipeline.");
        }

        if (deploy.Target != Target.Package)
        {
            return;
        }

        var feed = deploy.PublishFeed;
        if (spec.Build.Stack is not (Stack.DotNet or Stack.Node) || spec.Build.CustomCommands.Count > 0)
        {
            error("Publishing a package is for .NET (NuGet) and Node.js (npm) builds.");
        }
        else if (feed is null)
        {
            error("Choose the feed the package is published to.");
        }
        else if (feed.Type != (spec.Build.Stack == Stack.DotNet ? FeedType.NuGet : FeedType.Npm))
        {
            error("The feed to publish to is NuGet for .NET and npm for Node.js.");
        }
        else
        {
            // The same checks as a feed the build reads from.
            var probe = new PipelineSpec { Build = new() { Stack = spec.Build.Stack, PackageFeeds = [feed] } };
            feed.Name = Commands.PublishName;
            feed.Scope = feed.Type == FeedType.Npm && feed.Kind == FeedKind.GitHubPackages ? feed.Scope ?? "@" + feed.Organization?.ToLowerInvariant() : feed.Scope;
            CheckFeeds(probe, error, _ => { });
        }
    }

    private static void CheckAgent(PipelineSpec spec, Action<string> error)
    {
        var build = spec.Build;
        if (spec.IsInfrastructure)
        {
            return;
        }

        if ((build.Lint || build.SecurityScan || build.Sbom || build.CodeScan) && !Quality.Applies(spec))
        {
            error("The code style check, the security scan and the list of components run in bash on Linux build agents, for applications.");
        }
        else if (build.Lint && (!Quality.CanLint(build.Stack) || build.CustomCommands.Count > 0))
        {
            error("The code style check is for .NET, Node.js, Python, Go, Rust and Ruby builds.");
        }

        if (spec.Deploy.SignImage && !(spec.IsGenericRegistry || spec.Deploy.Target is Target.AwsEcs or Target.AwsEks))
        {
            error("Signing the image is for the images the pipeline pushes with Docker: ECR (ECS, EKS), Docker Hub, GitHub Container Registry or another registry.");
        }

        if (spec.IsMacBuild != (build.Stack == Stack.Ios) && build.CustomCommands.Count == 0)
        {
            error(build.Stack == Stack.Ios ? "iOS builds with Xcode: choose a macOS build agent." : "A macOS build agent is for iOS builds (or your own build commands).");
        }

        if (build.MobileFramework != MobileFramework.Native && build.Stack is not (Stack.Android or Stack.Ios))
        {
            error("React Native and .NET MAUI are ways to build an Android or iOS app.");
        }

        if (build.Stack == Stack.Msix && !spec.IsWindowsBuild)
        {
            error("An MSIX package builds with MSBuild: choose a Windows build agent.");
        }

        if (build.CustomCommands.Count == 0)
        {
            switch (build.Stack)
            {
                case Stack.Android or Stack.Ios when build.MobileFramework == MobileFramework.Maui:
                    if (!PathPattern().IsMatch(build.ProjectPath ?? ""))
                    {
                        error(".NET MAUI: write the project to publish (for example src/App/App.csproj).");
                    }

                    break;
                case Stack.Ios when !PathPattern().IsMatch(build.ProjectPath ?? "") || !Resource().IsMatch(build.Scheme ?? "") || !PathPattern().IsMatch(build.ExportOptions ?? ""):
                    error("iOS: write the workspace or project (App.xcworkspace or App.xcodeproj), the scheme and the export options file.");
                    break;
                case Stack.Msix when !PathPattern().IsMatch(build.ProjectPath ?? ""):
                    error("MSIX: write the packaging project (for example src/App.Package/App.Package.wapproj).");
                    break;
            }
        }

        if (spec.IsStore)
        {
            var (stack, name) = spec.Deploy.Target switch
            {
                Target.GooglePlay => (Stack.Android, "Google Play takes an Android build"),
                Target.AppStore => (Stack.Ios, "TestFlight takes an iOS build"),
                _ => (Stack.Msix, "The Microsoft Store takes an MSIX build"),
            };
            if (build.Stack != stack && build.CustomCommands.Count == 0)
            {
                error($"{name} (or your own build commands that leave the package in out/app).");
            }

            if (!AppIdentifier().IsMatch(spec.Deploy.AppId ?? ""))
            {
                error("Write the application's identifier in the store (the Android package name, the iOS bundle identifier or the Microsoft Store product ID).");
            }

            foreach (var environment in spec.Environments)
            {
                var value = environment.ResourceName ?? "";
                if (spec.Deploy.Target == Target.GooglePlay ? !Kubernetes().IsMatch(value) : value.Length > 0 && !WindowsName().IsMatch(value))
                {
                    error(spec.Deploy.Target switch
                    {
                        Target.GooglePlay => $"Environment {environment.Name}: write its Google Play track (internal, alpha, beta or production).",
                        Target.AppStore => $"Environment {environment.Name}: the TestFlight group is a name without quotes.",
                        _ => $"Environment {environment.Name}: the flight is its ID, without spaces or quotes.",
                    });
                }
            }

            if (spec.Platforms.Contains(Platform.Bitbucket) && spec.DeploysOnMac && build.Agent != BuildAgent.SelfHostedMac)
            {
                error("Bitbucket Pipelines has no hosted macOS: build and upload iOS apps on your own macOS runner.");
            }
        }

        if (build.Agent is BuildAgent.SelfHostedLinux or BuildAgent.SelfHostedWindows or BuildAgent.SelfHostedMac && !Label().IsMatch(build.AgentLabel ?? ""))
        {
            error("Write the label of your build agents (GitHub runner label, Azure DevOps agent pool, GitLab runner tag).");
        }

        if (build.Stack == Stack.DotNetFramework && !spec.IsWindowsBuild)
        {
            error(".NET Framework builds on Windows: choose a Windows build agent.");
        }

        if (spec.IsWindowsBuild)
        {
            if (spec.IsContainer)
            {
                error("Container images are built on Linux agents: choose a Linux build agent.");
            }
            else if (build.CustomCommands.Count == 0 && build.Stack is not (Stack.DotNet or Stack.DotNetFramework or Stack.Msix or Stack.None))
            {
                error("On a Windows build agent the build is .NET, .NET Framework or your own commands (PowerShell); the other languages build on Linux agents.");
            }
        }
    }

    private static void CheckDatabase(PipelineSpec spec, Action<string> error, Action<string> warning)
    {
        var deploy = spec.Deploy;
        if (deploy.SqlTrustedConnection && !(deploy.Migrations == MigrationKind.SqlScripts && spec.OnServerRunner && spec.UsesPowerShell))
        {
            error("Connecting as the runner's Windows account is only for SQL scripts run on your servers.");
        }

        if (deploy.Migrations == MigrationKind.None)
        {
            return;
        }

        if (spec.IsInfrastructure)
        {
            error("Database migrations run with an application deploy, not with Terraform or Bicep.");
            return;
        }

        if (deploy.Migrations == MigrationKind.Dacpac)
        {
            if (!spec.UsesPackage || !DacpacFile().IsMatch(deploy.DacpacPath ?? ""))
            {
                error("Write the .dacpac file inside the build (for example Database.dacpac); the build must leave it in out/app.");
            }
        }
        else if (deploy.Migrations == MigrationKind.EfCoreBundle)
        {
            if (spec.Build.Stack != Stack.DotNet || spec.Build.CustomCommands.Count > 0)
            {
                error("Entity Framework Core migrations need a .NET build (not your own build commands).");
            }

            if (!PathPattern().IsMatch(deploy.MigrationsProject ?? ""))
            {
                error("Write the project with the Entity Framework Core migrations (for example src/Data/Data.csproj).");
            }
        }
        else
        {
            if (!PathPattern().IsMatch(deploy.MigrationsFolder ?? "") || deploy.MigrationsFolder == ".")
            {
                error("Write the folder with the .sql files (for example db/migrations).");
            }

            warning("Every .sql file runs on each deploy, in name order: write them so that running them again changes nothing.");
        }
    }

    private static void CheckSettings(PipelineSpec spec, EnvironmentSpec environment, string label, Action<string> error)
    {
        if (environment.Servers.Count > 0)
        {
            if (!spec.OnServerRunner)
            {
                error($"{label}: a list of servers is only for deploys that run on your servers.");
            }
            else if (environment.Servers.Any(server => !Label().IsMatch(server ?? "")) || environment.Servers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != environment.Servers.Count)
            {
                error($"{label}: each server is one runner label or tag, without spaces, written once.");
            }
        }

        if (environment.Settings.Count == 0)
        {
            return;
        }

        if (spec.Deploy.Target is not (Target.AppService or Target.FunctionApp or Target.ContainerApp or Target.WindowsService or Target.Iis or Target.ScheduledTask or Target.LinuxService or Target.Script))
        {
            error($"{label}: settings are for App Service, Functions, Container Apps, Windows services, IIS and your own deploy commands.");
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in environment.Settings)
        {
            if (!SettingName().IsMatch(setting.Name ?? ""))
            {
                error($"{label}: a setting name uses letters, digits, dots, dashes and underscores, with : between levels (ConnectionStrings:Default).");
                continue;
            }

            if (!names.Add(setting.Name!))
            {
                error($"{label}: the setting {setting.Name} appears twice.");
            }

            var secret = !string.IsNullOrWhiteSpace(setting.Secret);
            if (secret && !string.IsNullOrEmpty(setting.Value))
            {
                error($"{label}: the setting {setting.Name} has a value and a secret; keep one.");
            }
            else if (secret && !SecretName().IsMatch(setting.Secret!))
            {
                error($"{label}: the secret of {setting.Name} is a name in capitals, digits and underscores (DB_CONNECTION).");
            }
            else if (!secret && ((setting.Value ?? "").Length > 500 || (setting.Value ?? "").IndexOfAny(['$', '\n', '\r']) >= 0))
            {
                error($"{label}: the value of {setting.Name} is one line of at most 500 characters without $; put anything else in a secret.");
            }
        }
    }

    private static void CheckServers(PipelineSpec spec, Action<string> error)
    {
        var deploy = spec.Deploy;
        if (spec.IsServerApp)
        {
            if (deploy.KeepOnServer.Any(name => !KeepName().IsMatch(name ?? "")))
            {
                error("The files and folders to keep are names (wildcards allowed) without slashes or quotes, such as appsettings.Production.json or logs.");
            }

            if (!PathPattern().IsMatch(deploy.SettingsFile ?? "") || deploy.SettingsFile == ".")
            {
                error("The settings file is a path inside the build, such as appsettings.json or Web.config.");
            }

            if (deploy.StopTimeoutSeconds is < 10 or > 3600)
            {
                error("The time to stop and start is 10 to 3600 seconds.");
            }

            if (deploy.ServiceExecutable is { Length: > 0 } program && (deploy.Target != Target.WindowsService || !Executable().IsMatch(program)))
            {
                error("The program of the service is an .exe inside its folder, such as Billing.Worker.exe.");
            }
        }

        if (deploy.Target == Target.Script)
        {
            if (deploy.DeployCommands.Count == 0)
            {
                error("Write the deploy commands, one per line.");
            }
            else if (deploy.DeployCommands.Any(command => command.Contains('\n', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(command)))
            {
                error("Each deploy command is one line.");
            }

            if (deploy.ScriptOnServer && deploy.ScriptSignsInToAzure)
            {
                error("On a server the script runs with the server's own access: Azure sign-in is only for hosted runners.");
            }
        }
    }

    public static string ResourceLabel(Target target) => target switch
    {
        Target.AppService => "web app name",
        Target.FunctionApp => "function app name",
        Target.StaticWebApp => "static web app name",
        Target.ContainerApp => "container app name",
        Target.Aks => "AKS cluster name",
        Target.WindowsService => "Windows service name",
        Target.Iis => "IIS site name",
        Target.ScheduledTask => "scheduled task name",
        Target.LinuxService => "systemd service name",
        _ => "resource name",
    };

    [GeneratedRegex(@"^[0-9]{1,4}(\.[0-9]{1,4}){0,2}$")]
    private static partial Regex VersionNumber();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex Repository();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$")]
    private static partial Regex Branch();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._()-]{0,89}$")]
    private static partial Regex Resource();

    [GeneratedRegex(@"^(?:\.|[A-Za-z0-9._][A-Za-z0-9._/-]{0,199})$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._/*-]{1,200}$")]
    private static partial Regex Glob();

    [GeneratedRegex(@"^\d{1,3}(?:\.\d{1,3})?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,150}$")]
    private static partial Regex AppIdentifier();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.-]+(?:/[A-Za-z0-9._-]+)+$")]
    private static partial Regex GitRepository();

    [GeneratedRegex(@"^([1-7])-([1-7]) ([01]\d|2[0-3])-([01]\d|2[0-4])$")]
    private static partial Regex DeployWindowPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]*(?::\d{1,5})?(?:/[a-z0-9._-]+)*$")]
    private static partial Regex RegistryAddress();

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}$")]
    private static partial Regex FullVersion();

    [GeneratedRegex("^[A-Za-z0-9]{5,50}$")]
    private static partial Regex RegistryPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._/-]{0,127}$")]
    private static partial Regex ImagePattern();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex Kubernetes();

    [GeneratedRegex("^[a-z0-9]{3,24}$")]
    private static partial Regex StoragePattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,39}$")]
    private static partial Regex FeedName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._()-]{0,79}$")]
    private static partial Regex WindowsName();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*(?::[A-Za-z0-9_.-]+)*$")]
    private static partial Regex SettingName();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$")]
    private static partial Regex SecretName();

    [GeneratedRegex("^[a-z0-9]{2,30}$")]
    private static partial Regex FunctionRuntime();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]{0,199}$")]
    private static partial Regex EntryPoint();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex HostName();

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,200}$")]
    private static partial Regex SonarKey();

    [GeneratedRegex("^/[A-Za-z0-9._/-]{1,250}$")]
    private static partial Regex LinuxPath();

    [GeneratedRegex("^[A-Za-z0-9@._-]{1,100}$")]
    private static partial Regex UnitName();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\*?$")]
    private static partial Regex TagPatternRule();

    [GeneratedRegex(@"^[A-Za-z0-9._][A-Za-z0-9._/-]{0,199}\.dacpac$", RegexOptions.IgnoreCase)]
    private static partial Regex DacpacFile();

    [GeneratedRegex(@"^[0-9*/,-]+(?: [0-9A-Za-z*/,-]+){4}$")]
    private static partial Regex Cron();

    // One slash, for a CircleCI resource class (namespace/name).
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}(?:/[A-Za-z0-9._-]{1,100})?$")]
    private static partial Regex Label();

    [GeneratedRegex(@"^[A-Za-z]:\\(?:[A-Za-z0-9 ._()-]+\\)*[A-Za-z0-9 ._()-]+$")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"^https?://[A-Za-z0-9.-]+(?::\d{1,5})?(?:/[A-Za-z0-9._~%/?=&+-]*)?$")]
    private static partial Regex HealthUrl();

    [GeneratedRegex(@"^[A-Za-z0-9._*-][A-Za-z0-9 ._*()-]{0,99}$")]
    private static partial Regex KeepName();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(?:\\[A-Za-z0-9._-]+)*\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex Executable();

    [GeneratedRegex(@"^https://[A-Za-z0-9.-]+(?::\d{1,5})?(?:/[A-Za-z0-9._~%/-]*)?$")]
    private static partial Regex FeedUrl();

    [GeneratedRegex("^@[a-z0-9][a-z0-9._-]{0,213}$")]
    private static partial Regex NpmScope();
}
