namespace CICDPipelineGenerator.Domain;

/// <summary>Checks the spec and, when nothing is wrong, writes one YAML file per chosen system plus SETUP.md.</summary>
public static class PipelineGenerator
{
    public static GenerationResult Generate(PipelineSpec spec)
    {
        var messages = PipelineRules.Check(spec);
        if (messages.Any(message => message.Severity == Severity.Error))
        {
            return new([], messages);
        }

        var files = new List<GeneratedFile>();
        foreach (var platform in spec.Platforms.Distinct().Order())
        {
            if (platform == Platform.CircleCi)
            {
                files.AddRange(CircleCiGenerator.GenerateAll(spec));
                continue;
            }

            if (SharedTemplates.Applies(spec) && SharedTemplates.Covers(platform))
            {
                files.AddRange(platform == Platform.GitHubActions ? SharedTemplates.GitHub(spec) : platform == Platform.AzureDevOps ? SharedTemplates.AzureDevOps(spec) : SharedTemplates.GitLab(spec));
                continue;
            }

            if (platform == Platform.CodePipeline)
            {
                files.AddRange(MoreSystems.CodeBuild(spec));
                continue;
            }

            files.Add(platform switch
            {
                Platform.GitHubActions => GitHubGenerator.Generate(spec),
                Platform.AzureDevOps => AzureDevOpsGenerator.Generate(spec),
                Platform.GitLab => GitLabGenerator.Generate(spec),
                Platform.Jenkins => JenkinsGenerator.Generate(spec),
                Platform.Bitbucket => BitbucketGenerator.Generate(spec),
                Platform.TravisCi => MoreSystems.Travis(spec),
                Platform.CloudBuild => MoreSystems.CloudBuild(spec),
                Platform.TeamCity => MoreSystems.TeamCity(spec),
                Platform.ArgoWorkflows => MoreSystems.Argo(spec),
                _ => CircleCiGenerator.Generate(spec),
            });
        }

        if (spec.Platforms.Contains(Platform.GitHubActions) && GitHubGenerator.Previews(spec))
        {
            files.Add(GitHubGenerator.PreviewCleanup(spec));
        }

        if (spec.Deploy.RollbackPipeline && spec.IsServerApp)
        {
            if (spec.Platforms.Contains(Platform.GitHubActions))
            {
                files.Add(GitHubGenerator.Rollback(spec));
            }

            if (spec.Platforms.Contains(Platform.AzureDevOps))
            {
                files.Add(AzureDevOpsGenerator.Rollback(spec));
            }

            if (spec.Platforms.Contains(Platform.Jenkins))
            {
                files.Add(JenkinsGenerator.Rollback(spec));
            }

            if (spec.Platforms.Contains(Platform.CloudBuild))
            {
                files.Add(MoreSystems.CloudBuildRollback(spec));
            }

            if (spec.Platforms.Contains(Platform.ArgoWorkflows))
            {
                files.Add(MoreSystems.Argo(spec, rollback: true));
            }
        }

        if (spec.Platforms.Contains(Platform.ArgoWorkflows) && !string.IsNullOrWhiteSpace(spec.Schedule))
        {
            files.Add(MoreSystems.ArgoCron(spec));
        }

        files.AddRange(SetupScript.All(spec));

        files.Add(SetupGuide.Generate(spec));
        return new(files, messages);
    }
}

/// <summary>Ready-made specs the screen opens, one per kind of deployment.</summary>
public static class Examples
{
    public static IReadOnlyList<string> Names { get; } = ["dotnet-app-service", "dotnet-private-feeds", "node-static-web-app", "container-app", "aks", "terraform", "bicep", "windows-service", "iis-site", "script"];

    /// <summary>The empty starting point: nothing of an example in it, not listed with the examples.</summary>
    public const string New = "new";

    public static PipelineSpec Get(string? name) => name switch
    {
        "dotnet-private-feeds" => new()
        {
            Name = "billing",
            Repository = "contoso/billing",
            Build = new()
            {
                Stack = Stack.DotNet,
                ProjectPath = "src/Billing/Billing.csproj",
                TestPath = "Billing.slnx",
                PackageFeeds =
                [
                    new() { Name = "contoso", Kind = FeedKind.AzureArtifacts, Organization = "contoso", Project = "platform", Feed = "packages" },
                    new() { Name = "jfrog", Kind = FeedKind.JFrog, Url = "https://contoso.jfrog.io", Feed = "nuget-virtual", JFrogOidcProvider = "github" },
                ],
            },
            Deploy = new() { Target = Target.AppService },
            Environments = Environments(environment => environment.ResourceName = $"app-billing-{environment.Name}"),
        },
        "windows-service" => new()
        {
            Name = "billing-worker",
            Repository = "contoso/billing-worker",
            Build = new() { Stack = Stack.DotNet, ProjectPath = "src/Billing.Worker/Billing.Worker.csproj", TestPath = "Billing.slnx" },
            Deploy = new() { Target = Target.WindowsService, KeepOnServer = ["appsettings.Production.json", "logs"] },
            Environments = Environments(environment =>
            {
                environment.ResourceName = "Billing.Worker";
                environment.ServerLabel = $"app-{environment.Name}";
            }),
        },
        "iis-site" => new()
        {
            Name = "portal",
            Repository = "contoso/portal",
            Build = new() { Stack = Stack.DotNet, ProjectPath = "src/Portal/Portal.csproj", TestPath = "Portal.slnx" },
            Deploy = new() { Target = Target.Iis, KeepOnServer = ["appsettings.Production.json", "App_Data", "logs"] },
            Environments = Environments(environment =>
            {
                environment.ResourceName = $"portal-{environment.Name}";
                environment.ServerLabel = $"web-{environment.Name}";
                environment.HealthCheckUrl = $"https://portal-{environment.Name}.contoso.com/health";
            }),
        },
        "script" => new()
        {
            Name = "reports",
            Repository = "contoso/reports",
            Build = new() { Stack = Stack.Node, WorkingDirectory = "web", OutputFolder = "dist" },
            Deploy = new()
            {
                Target = Target.Script,
                ScriptSignsInToAzure = true,
                DeployCommands =
                [
                    "az storage blob upload-batch --auth-mode login --account-name \"streports${DEPLOY_ENVIRONMENT}\" --destination '$web' --source out/app --overwrite",
                ],
            },
            Environments = Environments(_ => { }),
        },
        "node-static-web-app" => new()
        {
            Name = "web-site",
            Repository = "contoso/web-site",
            Build = new() { Stack = Stack.Node, WorkingDirectory = "web", OutputFolder = "dist" },
            Deploy = new() { Target = Target.StaticWebApp },
            Environments = Environments(environment => environment.ResourceName = $"swa-web-site-{environment.Name}"),
        },
        "container-app" => new()
        {
            Name = "orders-api",
            Repository = "contoso/orders-api",
            Build = new() { Stack = Stack.DotNet, TestPath = "OrdersApi.slnx" },
            Deploy = new() { Target = Target.ContainerApp, Registry = "contosoregistry", ImageName = "orders-api", Dockerfile = "src/OrdersApi/Dockerfile" },
            Environments = Environments(environment => environment.ResourceName = $"ca-orders-api-{environment.Name}"),
        },
        "aks" => new()
        {
            Name = "catalog",
            Repository = "contoso/catalog",
            Build = new() { Stack = Stack.Java },
            Deploy = new()
            {
                Target = Target.Aks,
                Registry = "contosoregistry",
                ImageName = "catalog",
                Namespace = "catalog",
                Manifests = "k8s",
                DeploymentName = "catalog",
                ContainerName = "catalog",
            },
            Environments = Environments(environment => environment.ResourceName = $"aks-contoso-{environment.Name}"),
        },
        "terraform" => new()
        {
            Name = "platform",
            Repository = "contoso/platform",
            Build = new() { Stack = Stack.None },
            Deploy = new() { Target = Target.Terraform, StateResourceGroup = "rg-terraform-state", StateStorageAccount = "contosotfstate" },
            Environments = Environments(environment => environment.ParametersFile = $"env/{environment.Name}.tfvars"),
        },
        "bicep" => new()
        {
            Name = "network",
            Repository = "contoso/network",
            Build = new() { Stack = Stack.None },
            Deploy = new() { Target = Target.Bicep, BicepTemplate = "infra/main.bicep" },
            Environments = Environments(environment => environment.ParametersFile = $"infra/{environment.Name}.bicepparam"),
        },
        New => new()
        {
            Name = "",
            Repository = "",
            Build = new() { Stack = Stack.DotNet, ProjectPath = "", TestPath = "" },
            Deploy = new() { Target = Target.AppService },
            Environments =
            [
                new() { Name = "dev", Branch = "develop" },
                new() { Name = "test", Branch = "release" },
                new() { Name = "prod", Branch = "main", RequireApproval = true },
            ],
        },
        _ => new()
        {
            Name = "my-app",
            Repository = "contoso/my-app",
            Build = new() { Stack = Stack.DotNet, ProjectPath = "src/MyApp/MyApp.csproj", TestPath = "MyApp.slnx" },
            Deploy = new() { Target = Target.AppService },
            Environments = Environments(environment => environment.ResourceName = $"app-my-app-{environment.Name}"),
        },
    };

    /// <summary>dev, test and prod, each with its own resource group and service connection; prod needs approval.</summary>
    private static List<EnvironmentSpec> Environments(Action<EnvironmentSpec> fill)
    {
        var environments = new List<EnvironmentSpec>();
        foreach (var (name, branch) in new[] { ("dev", "develop"), ("test", "release"), ("prod", "main") })
        {
            var environment = new EnvironmentSpec
            {
                Name = name,
                Branch = branch,
                RequireApproval = name == "prod",
                ResourceGroup = $"rg-contoso-{name}",
                ServiceConnection = $"azure-{name}",
            };
            fill(environment);
            environments.Add(environment);
        }

        return environments;
    }
}
