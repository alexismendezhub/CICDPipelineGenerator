using System.Text.RegularExpressions;
using CICDPipelineGenerator.Application.Import;
using CICDPipelineGenerator.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Infrastructure.Import;

public sealed partial class YamlPipelineImporter : IPipelineImporter
{
    // What tells the targets apart, in the order they are tried: the first whose text appears wins.
    private static readonly (string Marker, Target Target)[] Targets =
    [
        ("static-web-apps", Target.StaticWebApp), ("AzureStaticWebApp", Target.StaticWebApp),
        ("functions-action", Target.FunctionApp), ("AzureFunctionApp", Target.FunctionApp), ("az functionapp", Target.FunctionApp),
        ("webapps-deploy", Target.AppService), ("AzureWebApp", Target.AppService), ("AzureRmWebAppDeployment", Target.AppService), ("az webapp", Target.AppService),
        ("az containerapp", Target.ContainerApp),
        ("az aks", Target.Aks),
        ("gcloud container clusters", Target.GcpGke),
        ("gcloud run deploy", Target.GcpCloudRun), ("gcloud functions deploy", Target.GcpCloudFunctions), ("gcloud app deploy", Target.GcpAppEngine),
        ("aws ecs", Target.AwsEcs), ("elasticbeanstalk", Target.AwsElasticBeanstalk), ("aws lambda", Target.AwsLambda), ("aws s3 sync", Target.AwsS3),
        ("fastlane supply", Target.GooglePlay), ("fastlane pilot", Target.AppStore), ("msstore publish", Target.MicrosoftStore),
        ("aws eks", Target.AwsEks), ("aws cloudformation", Target.CloudFormation), ("cdk deploy", Target.AwsCdk), ("pulumi up", Target.Pulumi),
        ("terraform apply", Target.Terraform), ("terraform plan", Target.Terraform),
        ("az deployment", Target.Bicep),
        ("WebAppPool", Target.Iis), ("IISWebApp", Target.Iis),
        ("ScheduledTask", Target.ScheduledTask),
        ("Stop-Service", Target.WindowsService), ("Start-Service", Target.WindowsService),
        ("systemctl", Target.LinuxService),
        ("nuget push", Target.Package), ("npm publish", Target.Package),
        ("kubectl ", Target.Kubernetes), ("helm upgrade", Target.Kubernetes), ("docker push", Target.ContainerRegistry),
    ];

    public PipelineDraft Import(string yaml, IReadOnlyList<string>? related = null)
    {
        // The files opened with it (templates, included files, reusable workflows) are read for their commands; the
        // triggers and the environments come from the pipeline file itself, the first one that has them.
        var files = (related ?? []).Prepend(yaml).ToList();
        yaml = files.FirstOrDefault(file => MainFile().IsMatch(file)) ?? yaml;
        var text = string.Join('\n', files.OrderBy(file => file == yaml ? 0 : 1));
        YamlMappingNode root;
        var jenkins = JenkinsPipeline().IsMatch(yaml);
        var teamCity = !jenkins && yaml.Contains("jetbrains.buildServer", StringComparison.Ordinal);
        if (jenkins || teamCity)
        {
            // A Jenkinsfile is Groovy and TeamCity's settings are Kotlin, not YAML: their stages, conditions and commands are read as text.
            root = [];
        }
        else
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            root = stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode map
                ? map
                : throw new ImportException("The file is not a pipeline: it has no settings at the top.");
        }
        catch (YamlException exception)
        {
            throw new ImportException($"The file is not valid YAML: {exception.Message}");
        }

        var review = new List<ReviewItem>();
        var questions = new List<string>();
        var platform = jenkins ? Platform.Jenkins
            : teamCity ? Platform.TeamCity
            : Has(root, "phases") && Has(root, "version") ? Platform.CodePipeline
            : Text(Get(root, "kind")) is "WorkflowTemplate" or "Workflow" or "CronWorkflow" or "ClusterWorkflowTemplate" ? Platform.ArgoWorkflows
            : IsCloudBuild(root) ? Platform.CloudBuild
            : (Get(root, "jobs") is YamlMappingNode travis && Has(travis, "include")) || Has(root, "language") || Has(root, "dist") ? Platform.TravisCi
            : Has(root, "pipelines") ? Platform.Bitbucket
            : Has(root, "workflows") && Has(root, "jobs") && !Has(root, "on") && !Has(root, "true") ? Platform.CircleCi
            : root.Children.ContainsKey(new YamlScalarNode("jobs")) && (Has(root, "on") || Has(root, "true")) ? Platform.GitHubActions
            : Has(root, "trigger") || Has(root, "pool") || (Get(root, "stages") is YamlSequenceNode stages && stages.Children.FirstOrDefault() is YamlMappingNode) || Has(root, "steps") || Has(root, "jobs") ? Platform.AzureDevOps
            : Platform.GitLab;
        var spec = new PipelineSpec { Platforms = [platform], Repository = null };

        // What is built.
        spec.Build.Stack = Contains(text, "bundleRelease") ? Stack.Android
            : Contains(text, "xcodebuild") ? Stack.Ios
            : Contains(text, "AppxBundle") ? Stack.Msix
            : Contains(text, "flutter build") || Contains(text, "flutter test") || Contains(text, "flutter pub") ? Stack.Flutter
            : Contains(text, "sbt ") ? Stack.Scala
            : Contains(text, "mix test") || Contains(text, "mix deps") || Contains(text, "mix compile") ? Stack.Elixir
            : Contains(text, "swift build") || Contains(text, "swift test") ? Stack.Swift
            : Contains(text, "cmake ") ? Stack.Cpp
            : Contains(text, "gradlew") || Contains(text, "gradle ") ? Stack.Gradle
            : Contains(text, "cargo ") ? Stack.Rust
            : Contains(text, "go build") || Contains(text, "go test") ? Stack.Go
            : Contains(text, "composer ") ? Stack.Php
            : Contains(text, "bundle install") || Contains(text, "bundle exec") ? Stack.Ruby
            : Contains(text, "msbuild") && !Contains(text, "dotnet build") ? Stack.DotNetFramework
            : Contains(text, "dotnet ") || Contains(text, "DotNetCoreCLI") ? Stack.DotNet
            : Contains(text, "npm ") || Contains(text, "yarn ") ? Stack.Node
            : Contains(text, "mvn ") || Contains(text, "Maven@") ? Stack.Java
            : Contains(text, "pip ") || Contains(text, "pytest") ? Stack.Python
            : Stack.None;
        spec.Build.Agent = spec.Build.Stack switch
        {
            Stack.DotNetFramework or Stack.Msix => BuildAgent.HostedWindows,
            Stack.Ios => BuildAgent.HostedMac,
            _ => BuildAgent.HostedLinux,
        };

        var project = ProjectFile().Match(text);
        if (project.Success)
        {
            spec.Build.ProjectPath = project.Groups[1].Value;
        }

        var solution = SolutionFile().Match(text);
        if (solution.Success)
        {
            spec.Build.TestPath = solution.Groups[1].Value;
        }

        spec.Build.RunTests = Contains(text, " test") || Contains(text, "pytest") || Contains(text, "vstest");
        ReadBuildExtras(spec, text, review);
        review.Add(new(ReviewArea.Build, $"The build was recognised as {Words(spec.Build.Stack.ToString())} from its commands; check the project, the version and the folders."));

        // Where it deploys.
        var found = Targets.Where(item => Contains(text, item.Marker)).Select(item => (Target?)item.Target).FirstOrDefault();
        spec.Deploy.Target = found ?? Target.Script;
        if (found is null)
        {
            spec.Deploy.DeployCommands = ["echo \"Write the deploy commands here\""];
            questions.Add("The deploy steps were not recognised: the target is \"Your own deploy commands\"; paste the commands that deploy.");
        }
        else
        {
            review.Add(new(ReviewArea.Deploy, $"The target was recognised as {Words(found.Value.ToString())} from the deploy steps; check its settings."));
        }

        if (spec.IsInfrastructure)
        {
            spec.Build.Stack = Stack.None;
        }

        ReadStrategy(spec, text, review);
        if (spec.IsKubernetes && Value(HelmChart(), text) is { } chart)
        {
            spec.Deploy.HelmChart = chart;
        }

        if (spec.Deploy.Target == Target.Terraform)
        {
            // Where the code and its state are, as the init and the steps say it.
            spec.Deploy.InfraDirectory = Value(TerraformFolder(), text) ?? spec.Deploy.InfraDirectory;
            spec.Deploy.TerraformVersion = Value(TerraformRelease(), text) ?? spec.Deploy.TerraformVersion;
            spec.Deploy.StateResourceGroup = Value(Backend("resource_group_name"), text);
            spec.Deploy.StateStorageAccount = Value(Backend("storage_account_name"), text);
            spec.Deploy.StateContainer = Value(Backend("container_name"), text) ?? spec.Deploy.StateContainer;
        }

        // The other files this one hands work to; without them their steps were not read.
        var missing = OtherFiles().Matches(yaml).Select(match => match.Groups[1].Value.Trim('"', '\'')).Distinct(StringComparer.Ordinal).Take(8).ToList();
        if (missing.Count > 0 && files.Count == 1)
        {
            questions.Add($"The file uses other files ({string.Join(", ", missing)}): open them together with it, so their steps are read too.");
        }

        if (Region().Match(text) is { Success: true } region)
        {
            spec.Deploy.CloudRegion = region.Groups[1].Value;
        }

        // When it runs.
        spec.BuildPullRequests = jenkins ? !Contains(text, "not { changeRequest() }") : Contains(text, "pull_request") || Contains(text, "merge_request_event") || (platform == Platform.AzureDevOps && !string.Equals(Text(Get(root, "pr")), "none", StringComparison.OrdinalIgnoreCase));
        spec.ManualRun = platform != Platform.GitHubActions || Contains(text, "workflow_dispatch");
        spec.NotifyOnFailure = Contains(text, "NOTIFY_WEBHOOK") && Contains(text, "failed");
        spec.NotifyOnSuccess = Contains(text, "NOTIFY_WEBHOOK") && Contains(text, "is deployed to");
        spec.Deploy.PreviewPullRequests = Contains(text, "pr-${{ github.event.number }}") || Contains(text, "review/$CI_MERGE_REQUEST_IID");
        var paths = platform == Platform.GitHubActions ? Get(Get(Get(root, "on") as YamlMappingNode ?? Get(root, "true") as YamlMappingNode, "push") as YamlMappingNode, "paths")
            : platform == Platform.AzureDevOps ? Get(Get(Get(root, "trigger") as YamlMappingNode, "paths") as YamlMappingNode, "include")
            : null;
        if (paths is YamlSequenceNode filters)
        {
            spec.PathFilters = [.. filters.Children.Select(Text).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!)];
        }

        var cron = Cron().Match(text);
        if (cron.Success)
        {
            spec.Schedule = cron.Groups[1].Value.Trim();
        }

        // The environments, in the order they appear, with the names their jobs use.
        var named = platform is Platform.TravisCi or Platform.CloudBuild or Platform.CodePipeline or Platform.TeamCity or Platform.ArgoWorkflows;
        var environments = jenkins
            ? JenkinsStage().Matches(yaml).Select(match => (match.Groups[1].Value.Trim(), (YamlMappingNode)[])).ToList()
            : named
            // These systems have no environments of their own: the names of the stages, steps and stores say them.
            ? NamedStage().Matches(text).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Where(name => name != "build").Distinct(StringComparer.Ordinal).Select(name => (name, (YamlMappingNode)[])).ToList()
            : Environments(root, platform).ToList();
        if (environments.Count == 0)
        {
            questions.Add("No environments were found in the file: dev and prod are proposed.");
        }
        else
        {
            spec.Environments = [];
            foreach (var (name, job) in environments)
            {
                var slug = Slug(name);
                if (spec.Environments.Any(existing => existing.Name == slug))
                {
                    continue;
                }

                var jobText = string.Join('\n', Scalars(job));
                var environment = new EnvironmentSpec
                {
                    Name = slug,
                    Branch = Value(BranchOf(), jobText) ?? spec.MainBranch,
                    ResourceName = Pair(job, "app-name", "appName", "WebAppName", "function-name") ?? Value(ServerName(), jobText) ?? Value(NameOf(), jobText),
                    TargetPath = spec.IsServerApp ? Value(ServerFolder(), jobText) : null,
                    ServerLabel = spec.IsServerApp ? RunnerLabel(job) : null,
                    ParametersFile = spec.Deploy.Target == Target.Terraform ? Value(VarFile(), jobText) : null,
                    ResourceGroup = Pair(job, "resourceGroupName", "resource-group") ?? Value(GroupOf(), jobText),
                    Slot = Pair(job, "slot-name", "slotName") ?? Value(SlotOf(), jobText),
                    ServiceConnection = Pair(job, "azureSubscription", "connectedServiceNameARM"),
                    RequireApproval = string.Equals(Pair(job, "when"), "manual", StringComparison.Ordinal),
                };
                spec.Environments.Add(environment);
            }

            var branches = spec.Environments.Select(environment => environment.Branch).Distinct(StringComparer.Ordinal).ToList();
            if (Contains(text, "refs/tags/") || Contains(text, "CI_COMMIT_TAG") || Get(Get(root, "on") as YamlMappingNode is { } on ? Get(on, "push") : null, "tags") is not null)
            {
                spec.Flow = Flow.Tag;
            }
            else if (branches.Count > 1)
            {
                spec.Flow = Flow.BranchPerEnvironment;
            }
            else
            {
                spec.Flow = Flow.Promotion;
                spec.MainBranch = branches[0];
            }

            if (platform != Platform.GitLab)
            {
                review.Add(new(ReviewArea.Environments, "Approvals are kept in the pipeline system, not in the file: mark the environments that need one."));
            }
        }

        // What the file does that these settings will not write.
        var unknown = Steps(root).Where(step => !Known().IsMatch(step)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        if (unknown.Count > 0)
        {
            questions.Add($"These steps of the file are not kept: {string.Join(", ", unknown)}. Add what they do as your own commands if it is needed.");
        }

        var summary = $"Read from a {Systems.Name(platform)} file: "
            + $"{Words(spec.Build.Stack.ToString())} to {Words(spec.Deploy.Target.ToString())}, {spec.Environments.Count} environment(s). It is a best guess: review it before approving.";
        return new PipelineDraft { Summary = summary, Spec = spec, Review = review, Questions = questions };
    }

    /// <summary>What the build does besides compiling and testing, read from the commands and the steps that do it.</summary>
    private static void ReadBuildExtras(PipelineSpec spec, string text, List<ReviewItem> review)
    {
        var build = spec.Build;
        if (build.Stack == Stack.None)
        {
            return;
        }

        var version = build.Stack switch
        {
            Stack.DotNet => DotNetVersion(),
            Stack.Node => NodeVersion(),
            Stack.Python => PythonVersion(),
            Stack.Java or Stack.Gradle => JavaVersion(),
            Stack.Go => GoVersion(),
            Stack.Ruby => RubyVersion(),
            Stack.Php => PhpVersion(),
            _ => null,
        };
        if (version?.Match(text) is { Success: true } match)
        {
            build.Version = match.Groups.Values.Skip(1).First(group => group.Success).Value;
        }

        // Each one is something the pipeline can write again; a version the file also tests with is a job named for it.
        build.Services = [.. ServiceImages.Where(item => Contains(text, item.Marker)).Select(item => item.Kind).Distinct()];
        build.AlsoTestWith = [.. TestedWith().Matches(text).Select(found => found.Groups[1].Success ? found.Groups[1].Value : found.Groups[2].Value.Replace('-', '.')).Where(other => other != build.EffectiveVersion).Distinct().Take(5)];
        build.CodeScan = Contains(text, "semgrep");
        build.SecurityScan = Contains(text, "trivy ");
        build.Sbom = Contains(text, "cyclonedx");
        build.CodeQl = Contains(text, "codeql-action");
        build.Coverage = Contains(text, "XPlat Code Coverage");
        build.AuditDependencies = Contains(text, "npm audit") || Contains(text, "--vulnerable") || Contains(text, "pip-audit") || Contains(text, "bundle-audit");
        build.CachePackages = Contains(text, "actions/cache@") || Contains(text, "Cache@2") || Contains(text, "restore_cache") || PackageCacheKey().IsMatch(text);
        build.Lint = Contains(text, "dotnet format") || Contains(text, "eslint") || Contains(text, "npm run lint") || Contains(text, "ruff ") || Contains(text, "golangci-lint") || Contains(text, "cargo fmt") || Contains(text, "rubocop");
        build.PublishTestResults = (Contains(text, "--logger trx") || Contains(text, "PublishTestResults@") || Contains(text, "--junitxml") || Contains(text, "junit:") || Contains(text, "store_test_results"))
            && (build.Stack == Stack.DotNet || build.WritesJUnit);
        if (SonarKey().Match(text) is { Success: true } sonar)
        {
            build.SonarProjectKey = sonar.Groups.Values.Skip(1).First(group => group.Success).Value;
        }

        foreach (Match source in NuGetSource().Matches(text))
        {
            if (build.Stack == Stack.DotNet && build.PackageFeeds.All(feed => feed.Name != source.Groups[2].Value))
            {
                build.PackageFeeds.Add(new PackageFeed { Name = source.Groups[2].Value, Type = FeedType.NuGet, Kind = FeedKind.Other, Url = source.Groups[1].Value });
            }
        }

        var extras = new[]
        {
            build.Services.Count > 0 ? "services for the tests" : null, build.AlsoTestWith.Count > 0 ? "tests with other versions" : null,
            build.CodeScan || build.SecurityScan || build.CodeQl ? "scans" : null, build.PackageFeeds.Count > 0 ? "private feeds" : null,
            build.CachePackages ? "the package cache" : null, build.PublishTestResults || build.Coverage ? "test results" : null,
        }.Where(extra => extra is not null).ToList();
        if (extras.Count > 0)
        {
            review.Add(new(ReviewArea.Build, $"Also read from the file: {string.Join(", ", extras)}."));
        }
    }

    private static readonly (string Marker, ServiceKind Kind)[] ServiceImages =
    [
        ("postgres:", ServiceKind.Postgres), ("mysql:", ServiceKind.MySql), ("mssql/server", ServiceKind.SqlServer), ("redis:", ServiceKind.Redis), ("mongo:", ServiceKind.MongoDb),
    ];

    /// <summary>How a new version takes over, from the commands that move the traffic.</summary>
    private static void ReadStrategy(PipelineSpec spec, string text, List<ReviewItem> review)
    {
        var deploy = spec.Deploy;
        var canary = CanaryShare().Match(text);
        deploy.Strategy = canary.Success || Contains(text, "-canary") ? DeployStrategy.Canary
            : Contains(text, "slot swap") || Contains(text, "--revision-weight") || Contains(text, "--to-revisions") || Contains(text, "swap-environment-cnames") || Contains(text, "--routing-config") || Contains(text, "-green") ? DeployStrategy.BlueGreen
            : DeployStrategy.Standard;
        if (canary.Success && int.TryParse(canary.Groups.Values.Skip(1).First(group => group.Success).Value, out var share) && share is > 0 and < 100)
        {
            deploy.CanaryPercent = share;
        }

        if (deploy.Strategy != DeployStrategy.Standard && !Strategies.Supports(spec))
        {
            deploy.Strategy = DeployStrategy.Standard;
        }

        if (deploy.Strategy != DeployStrategy.Standard)
        {
            review.Add(new(ReviewArea.Deploy, $"The new version takes over with {(deploy.Strategy == DeployStrategy.Canary ? "a canary" : "blue-green")}, read from the commands that move the traffic."));
        }
    }

    private static IEnumerable<(string Name, YamlMappingNode Job)> Environments(YamlMappingNode root, Platform platform)
    {
        IEnumerable<YamlMappingNode> jobs = platform switch
        {
            Platform.GitHubActions => Get(root, "jobs") is YamlMappingNode map ? map.Children.Values.OfType<YamlMappingNode>() : [],
            Platform.AzureDevOps => Descendants(root).Where(node => Has(node, "deployment")),
            Platform.Bitbucket => Descendants(root).Where(node => Get(node, "deployment") is YamlScalarNode),
            Platform.CircleCi => Get(root, "jobs") is YamlMappingNode circle ? circle.Children.Where(pair => Text(pair.Key)?.StartsWith("deploy-", StringComparison.Ordinal) == true).Select(pair => pair.Value).OfType<YamlMappingNode>() : [],
            _ => root.Children.Values.OfType<YamlMappingNode>().Where(node => Has(node, "script")),
        };
        foreach (var job in jobs)
        {
            var name = platform == Platform.Bitbucket ? Text(Get(job, "deployment"))
                : platform == Platform.CircleCi ? Text(((YamlMappingNode)Get(root, "jobs")!).Children.First(pair => ReferenceEquals(pair.Value, job)).Key)?["deploy-".Length..]
                : Get(job, "environment") switch
            {
                YamlScalarNode scalar => scalar.Value,
                YamlMappingNode map => Text(Get(map, "name")),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(name) && !name.Contains('$', StringComparison.Ordinal))
            {
                // Azure DevOps writes environment.resource for a resource of the environment.
                yield return (name.Split('.')[0], job);
            }
        }
    }

    /// <summary>The actions (GitHub) and tasks (Azure DevOps) the file uses.</summary>
    private static IEnumerable<string> Steps(YamlMappingNode root) =>
        Descendants(root).SelectMany(node => new[] { Text(Get(node, "uses")), Text(Get(node, "task")) }).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!);

    private static IEnumerable<YamlMappingNode> Descendants(YamlNode node)
    {
        if (node is YamlMappingNode map)
        {
            yield return map;
        }

        foreach (var child in node is YamlMappingNode mapping ? mapping.Children.Values : node is YamlSequenceNode sequence ? sequence.Children : [])
        {
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<string> Scalars(YamlNode node) => node switch
    {
        YamlScalarNode scalar => [scalar.Value ?? ""],
        YamlMappingNode map => map.Children.Values.SelectMany(Scalars),
        YamlSequenceNode sequence => sequence.Children.SelectMany(Scalars),
        _ => [],
    };

    /// <summary>The first value written under any of these keys inside the job (an input of an action or a task).</summary>
    private static string? Pair(YamlMappingNode job, params string[] keys) =>
        Descendants(job).SelectMany(node => keys.Select(key => Text(Get(node, key)))).FirstOrDefault(value => Usable(value));

    private static string? Value(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        // A name that ends in a dash had a variable after it (network-dev-$(Build.BuildId)): it is not the whole name.
        return match.Success && Usable(match.Groups[1].Value) && !match.Groups[1].Value.EndsWith('-') ? match.Groups[1].Value.Trim('"', '\'') : null;
    }

    /// <summary>A literal name: a value taken from a variable or an expression cannot be carried over.</summary>
    private static bool Usable(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('$', StringComparison.Ordinal);

    private static YamlNode? Get(YamlNode? node, string key) =>
        node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static bool Has(YamlMappingNode node, string key) => node.Children.ContainsKey(new YamlScalarNode(key));

    private static string? Text(YamlNode? node) => (node as YamlScalarNode)?.Value;

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>ContainerApp → "Container App", for the texts people read.</summary>
    private static string Words(string name) => name == "DotNet" ? ".NET" : name == "None" ? "no build" : CapitalInside().Replace(name, " $1");

    /// <summary>The label of the person's own runner the job asks for: the last one of runs-on or tags that is not a kind of runner.</summary>
    private static string? RunnerLabel(YamlMappingNode job) =>
        RunnerKeys.Select(key => Get(job, key)).OfType<YamlSequenceNode>()
            .SelectMany(sequence => sequence.Children.Select(Text))
            .LastOrDefault(label => Usable(label) && label is not ("self-hosted" or "self.hosted" or "windows" or "linux" or "macos"));

    private static readonly string[] RunnerKeys = ["runs-on", "tags"];

    private static Regex Backend(string setting) => new($@"-backend-config=""?{setting}=([A-Za-z0-9._-]+)", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static string Slug(string name)
    {
        var slug = NotSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 || !char.IsLetter(slug[0]) ? "env-" + slug : slug;
    }

    [GeneratedRegex(@"dotnet (?:publish|pack|build) (\S+\.csproj)")]
    private static partial Regex ProjectFile();

    [GeneratedRegex(@"(?:dotnet (?:restore|build|test)|msbuild(?:\.exe)?['""]?|\$msbuild) (\S+\.slnx?)", RegexOptions.IgnoreCase)]
    private static partial Regex SolutionFile();

    [GeneratedRegex(@"(?:aws-region:|--region|AWS_REGION:)\s*([a-z]{2}-[a-z0-9-]+\d)")]
    private static partial Regex Region();

    [GeneratedRegex(@"dotnet-version:\s*['""]?(\d+\.\d+)|dotnet/sdk:(\d+\.\d+)|UseDotNet@2[\s\S]{0,160}?version:\s*['""]?(\d+\.\d+)")]
    private static partial Regex DotNetVersion();

    [GeneratedRegex(@"node-version:\s*['""]?(\d+)|\bnode:(\d+)|versionSpec:\s*['""]?(\d+)")]
    private static partial Regex NodeVersion();

    [GeneratedRegex(@"python-version:\s*['""]?(\d+\.\d+)|\bpython:(\d+\.\d+)")]
    private static partial Regex PythonVersion();

    [GeneratedRegex(@"java-version:\s*['""]?(\d+)|temurin-(\d+)|\bjdk(\d+)\b")]
    private static partial Regex JavaVersion();

    [GeneratedRegex(@"go-version:\s*['""]?(\d+\.\d+)|golang:(\d+\.\d+)")]
    private static partial Regex GoVersion();

    [GeneratedRegex(@"ruby-version:\s*['""]?(\d+\.\d+)|\bruby:(\d+\.\d+)")]
    private static partial Regex RubyVersion();

    [GeneratedRegex(@"php-version:\s*['""]?(\d+\.\d+)|\bphp:(\d+\.\d+)")]
    private static partial Regex PhpVersion();

    // The name of the job, or (GitLab, CircleCI) its key: test-8-0.
    [GeneratedRegex(@"Test with (\d+(?:\.\d+){0,2})|^\s*test-(\d+(?:-\d+){0,2}):", RegexOptions.Multiline)]
    private static partial Regex TestedWith();

    [GeneratedRegex(@"^\s*(?:cache|caches):", RegexOptions.Multiline)]
    private static partial Regex PackageCacheKey();

    [GeneratedRegex(@"/k:\\?""([A-Za-z0-9_.:-]+)|sonar\.projectKey=([A-Za-z0-9_.:-]+)")]
    private static partial Regex SonarKey();

    [GeneratedRegex(@"dotnet nuget add source ""(https://[^""\s]+)"" --name ([A-Za-z0-9_]+)")]
    private static partial Regex NuGetSource();

    [GeneratedRegex(@"\$new=(\d+)""|candidate=(\d+)|AdditionalVersionWeights=\{\$version=0\.(\d+)\}|traffic-routing set[^\n]*=(\d+)")]
    private static partial Regex CanaryShare();

    [GeneratedRegex(@"cron(?::\s*|\()['""]?([0-9H*/,-]+(?: [0-9A-Za-z*/,-]+){4})['""]?")]
    private static partial Regex Cron();

    [GeneratedRegex(@"(?:refs/heads/|CI_COMMIT_BRANCH == ""|\bbranch ')([A-Za-z0-9._/-]+)")]
    private static partial Regex BranchOf();

    [GeneratedRegex(@"(?:--name|--function-name|--environment-name|--services?|gcloud run deploy|gcloud functions deploy|s3://)\s*([A-Za-z0-9._()""'-]+)")]
    private static partial Regex NameOf();

    [GeneratedRegex(@"(?:--resource-group|--cluster|--application-name)\s+([A-Za-z0-9._()""'-]+)")]
    private static partial Regex GroupOf();

    [GeneratedRegex(@"--slot\s+([A-Za-z0-9._()""'-]+)")]
    private static partial Regex SlotOf();

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex CapitalInside();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    /// <summary>Cloud Build: steps that name the image they run in, and nothing of Azure DevOps.</summary>
    private static bool IsCloudBuild(YamlMappingNode root) =>
        Has(root, "availableSecrets") || Has(root, "substitutions")
        || (!Has(root, "trigger") && !Has(root, "pool") && !Has(root, "stages") && Get(root, "steps") is YamlSequenceNode steps
            && steps.Children.FirstOrDefault() is YamlMappingNode first && !Has(first, "task") && !Has(first, "displayName")
            && Text(Get(first, "name")) is { } image && (image.Contains('/', StringComparison.Ordinal) || image.Contains(':', StringComparison.Ordinal))
            && (Has(first, "args") || Has(first, "script") || Has(first, "entrypoint")));

    [GeneratedRegex(@"(?:name: Deploy to |name = ""Deploy to |id: deploy-|template: deploy-)([a-z][a-z0-9-]*)|: /[a-z0-9-]+/([a-z][a-z0-9-]*)/[A-Z][A-Z0-9_]*\s*$", RegexOptions.Multiline)]
    private static partial Regex NamedStage();

    [GeneratedRegex(@"^\s*pipeline\s*\{", RegexOptions.Multiline)]
    private static partial Regex JenkinsPipeline();

    [GeneratedRegex(@"stage\('Deploy to ([A-Za-z0-9_-]+)")]
    private static partial Regex JenkinsStage();

    // A file that says when it runs is the pipeline; a template or an included file only has steps or jobs.
    [GeneratedRegex(@"^(?:on|trigger|pr|workflow|pipelines|workflows|stages):", RegexOptions.Multiline)]
    private static partial Regex MainFile();

    // The name of a Windows service, an IIS site, a scheduled task or a systemd service, as the deploy scripts write it.
    [GeneratedRegex(@"(?:\$(?:name|siteName|serviceName) = '|(?:Stop|Start|Restart)-Service(?: -Name)? '?|(?:Stop|Start)-WebAppPool(?: -Name)? '?|systemctl (?:stop|start|restart) |^\s*name=')([A-Za-z0-9][A-Za-z0-9 ._@-]*?)(?='|\s*$|\s*;)", RegexOptions.Multiline)]
    private static partial Regex ServerName();

    [GeneratedRegex(@"(?:\$target = '|^\s*target=')([A-Za-z]:\\[^'\r\n]+|/[^'\r\n]+)'", RegexOptions.Multiline)]
    private static partial Regex ServerFolder();

    [GeneratedRegex(@"(?:working-directory:\s*|cd\s+)([A-Za-z0-9._/-]+)\s*\n(?:[^\n]*\n){0,3}?[^\n]*terraform ")]
    private static partial Regex TerraformFolder();

    [GeneratedRegex(@"(?:terraform_version:\s*['""]?|releases\.hashicorp\.com/terraform/|hashicorp/terraform:)(\d+\.\d+\.\d+)")]
    private static partial Regex TerraformRelease();

    [GeneratedRegex(@"-var-file=([A-Za-z0-9._/-]+)")]
    private static partial Regex VarFile();

    [GeneratedRegex(@"helm upgrade --install \S+ ([A-Za-z0-9._/-]+)")]
    private static partial Regex HelmChart();

    // Templates (Azure DevOps), included files (GitLab) and reusable workflows (GitHub) kept in the repository.
    [GeneratedRegex(@"(?:template:|local:|uses:)\s*['""]?(\.?/?[A-Za-z0-9._/-]+\.ya?ml)")]
    private static partial Regex OtherFiles();

    // The steps the settings write themselves: tool set-up, artifacts, sign-in, deploys and plain scripts.
    [GeneratedRegex(@"^(?:actions/(?:checkout|setup-|upload-artifact|download-artifact|cache)|azure/|Azure/|hashicorp/setup-terraform|aws-actions/configure-aws-credentials|google-github-actions/|jfrog/setup-jfrog-cli|UseDotNet|NodeTool|UseNode|UsePythonVersion|JavaToolInstaller|PublishPipelineArtifact|DownloadPipelineArtifact|PublishBuildArtifacts|DownloadBuildArtifacts|AzureCLI|AzureWebApp|AzureRmWebAppDeployment|AzureFunctionApp|AzureStaticWebApp|DotNetCoreCLI|NuGetAuthenticate|npmAuthenticate|PublishTestResults|PublishCodeCoverageResults|Cache|Bash|PowerShell|CmdLine|Npm|Maven|VSBuild|VSTest|NuGetCommand|NuGetToolInstaller|CopyFiles|ArchiveFiles)", RegexOptions.IgnoreCase)]
    private static partial Regex Known();
}
