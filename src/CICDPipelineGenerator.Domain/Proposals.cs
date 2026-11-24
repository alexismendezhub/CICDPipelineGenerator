using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CICDPipelineGenerator.Domain;

/// <summary>
/// Nothing is applied until the person approves: the screen shows the proposed settings, what changes compared to
/// the current ones, what the AI assumed (needs review), its questions, and the checks on the proposal.
/// </summary>
public sealed record PipelineProposal(
    string Summary,
    PipelineSpec Spec,
    IReadOnlyList<string> Changes,
    IReadOnlyList<ReviewItem> Review,
    IReadOnlyList<string> Questions,
    IReadOnlyList<Message> Messages);

public static class ProposalBuilder
{
    /// <summary>baseline is the settings before any proposal (null when starting fresh); the changes compare to it.</summary>
    public static PipelineProposal Build(PipelineDraft draft, PipelineSpec? baseline)
    {
        var spec = Normalize(draft.Spec);
        return new(
            draft.Summary.Trim(),
            spec,
            baseline is null ? [] : Changes(baseline, spec),
            draft.Review.Where(item => !string.IsNullOrWhiteSpace(item.Text)).Select(item => item with { Text = item.Text.Trim() }).ToList(),
            draft.Questions.Select(question => question.Trim()).Where(question => question.Length > 0).ToList(),
            PipelineRules.Check(spec));
    }

    /// <summary>
    /// The AI answers "" for "not set" because every field is required in the schema: empty optional values become
    /// null, empty values that have a default get the default, and lists lose blank entries.
    /// </summary>
    public static PipelineSpec Normalize(PipelineSpec spec)
    {
        var defaults = new PipelineSpec();
        var build = spec.Build;
        var deploy = spec.Deploy;

        spec.Name = Trim(spec.Name) ?? defaults.Name;
        spec.Repository = Trim(spec.Repository);
        spec.Platforms = spec.Platforms.Distinct().ToList();
        spec.MainBranch = Trim(spec.MainBranch) ?? defaults.MainBranch;
        spec.PathFilters = Lines(spec.PathFilters);
        spec.Schedule = Trim(spec.Schedule);
        spec.TagPattern = Trim(spec.TagPattern) ?? defaults.TagPattern;

        build.Version = Trim(build.Version);
        build.WorkingDirectory = Trim(build.WorkingDirectory) ?? defaults.Build.WorkingDirectory;
        build.ProjectPath = Trim(build.ProjectPath);
        build.TestPath = Trim(build.TestPath);
        build.OutputFolder = Trim(build.OutputFolder) ?? defaults.Build.OutputFolder;
        build.CustomCommands = Lines(build.CustomCommands);
        build.AgentLabel = Trim(build.AgentLabel);
        build.SonarProjectKey = Trim(build.SonarProjectKey);
        build.Scheme = Trim(build.Scheme);
        build.ExportOptions = Trim(build.ExportOptions) ?? defaults.Build.ExportOptions;
        deploy.AppId = Trim(deploy.AppId);
        deploy.GitOpsRepository = Trim(deploy.GitOpsRepository);
        foreach (var feed in build.PackageFeeds)
        {
            feed.Name = Trim(feed.Name) ?? "";
            feed.Organization = Trim(feed.Organization);
            feed.Project = Trim(feed.Project);
            feed.Feed = Trim(feed.Feed);
            feed.Url = Trim(feed.Url);
            feed.Scope = Trim(feed.Scope);
            feed.JFrogOidcProvider = Trim(feed.JFrogOidcProvider);
        }

        deploy.Registry = Trim(deploy.Registry);
        deploy.ImageName = Trim(deploy.ImageName);
        deploy.Dockerfile = Trim(deploy.Dockerfile) ?? defaults.Deploy.Dockerfile;
        deploy.DockerContext = Trim(deploy.DockerContext) ?? defaults.Deploy.DockerContext;
        deploy.Namespace = Trim(deploy.Namespace) ?? defaults.Deploy.Namespace;
        deploy.Manifests = Trim(deploy.Manifests) ?? defaults.Deploy.Manifests;
        deploy.DeploymentName = Trim(deploy.DeploymentName);
        deploy.HelmChart = Trim(deploy.HelmChart);
        deploy.CanaryPercent = deploy.CanaryPercent <= 0 ? defaults.Deploy.CanaryPercent : deploy.CanaryPercent;
        deploy.CanaryMinutes = deploy.CanaryMinutes <= 0 ? defaults.Deploy.CanaryMinutes : deploy.CanaryMinutes;
        deploy.ContainerName = Trim(deploy.ContainerName);
        deploy.InfraDirectory = Trim(deploy.InfraDirectory) ?? defaults.Deploy.InfraDirectory;
        deploy.TerraformVersion = Trim(deploy.TerraformVersion) ?? defaults.Deploy.TerraformVersion;
        deploy.StateResourceGroup = Trim(deploy.StateResourceGroup);
        deploy.StateStorageAccount = Trim(deploy.StateStorageAccount);
        deploy.StateContainer = Trim(deploy.StateContainer) ?? defaults.Deploy.StateContainer;
        deploy.BicepTemplate = Trim(deploy.BicepTemplate) ?? defaults.Deploy.BicepTemplate;
        deploy.KeepOnServer = Lines(deploy.KeepOnServer);
        deploy.StopTimeoutSeconds = deploy.StopTimeoutSeconds <= 0 ? defaults.Deploy.StopTimeoutSeconds : deploy.StopTimeoutSeconds;
        deploy.ServiceExecutable = Trim(deploy.ServiceExecutable);
        deploy.DeployCommands = Lines(deploy.DeployCommands);
        deploy.SettingsFile = Trim(deploy.SettingsFile) ?? defaults.Deploy.SettingsFile;
        deploy.MigrationsProject = Trim(deploy.MigrationsProject);
        deploy.DacpacPath = Trim(deploy.DacpacPath);
        deploy.CloudRegion = Trim(deploy.CloudRegion);
        deploy.Bucket = Trim(deploy.Bucket);
        deploy.FunctionRuntime = Trim(deploy.FunctionRuntime);
        deploy.FunctionEntryPoint = Trim(deploy.FunctionEntryPoint);
        deploy.AppYaml = Trim(deploy.AppYaml) ?? defaults.Deploy.AppYaml;
        if (deploy.Target != Target.Package)
        {
            deploy.PublishFeed = null;
        }
        else if (deploy.PublishFeed is { } publish)
        {
            publish.Name = Commands.PublishName;
            publish.Organization = Trim(publish.Organization);
            publish.Project = Trim(publish.Project);
            publish.Feed = Trim(publish.Feed);
            publish.Url = Trim(publish.Url);
            publish.Scope = Trim(publish.Scope);
            publish.JFrogOidcProvider = Trim(publish.JFrogOidcProvider);
        }
        deploy.MigrationsFolder = Trim(deploy.MigrationsFolder) ?? defaults.Deploy.MigrationsFolder;

        foreach (var environment in spec.Environments)
        {
            environment.Name = Trim(environment.Name) ?? "";
            environment.Branch = Trim(environment.Branch) ?? spec.MainBranch;
            environment.ResourceGroup = Trim(environment.ResourceGroup);
            environment.ResourceName = Trim(environment.ResourceName);
            environment.Slot = Trim(environment.Slot);
            environment.ServiceConnection = Trim(environment.ServiceConnection);
            environment.ParametersFile = Trim(environment.ParametersFile);
            environment.ServerLabel = Trim(environment.ServerLabel);
            environment.TargetPath = Trim(environment.TargetPath);
            environment.AppPool = Trim(environment.AppPool);
            environment.HealthCheckUrl = Trim(environment.HealthCheckUrl);
            environment.DeployWindow = Trim(environment.DeployWindow);
            environment.EndToEndFolder = Trim(environment.EndToEndFolder);
            environment.LoadTestScript = Trim(environment.LoadTestScript);
            environment.BeforeDeploy = Lines(environment.BeforeDeploy);
            environment.AfterDeploy = Lines(environment.AfterDeploy);
            environment.Servers = Lines(environment.Servers);
            environment.Hosts = Lines(environment.Hosts);
            environment.Settings = environment.Settings.Where(setting => !string.IsNullOrWhiteSpace(setting.Name)).ToList();
            foreach (var setting in environment.Settings)
            {
                setting.Name = setting.Name.Trim();
                setting.Secret = Trim(setting.Secret);
                setting.Value = setting.Secret is null ? setting.Value ?? "" : null;
            }
        }

        return spec;
    }

    /// <summary>Each difference as one plain sentence; environments are matched by name, so a renamed one is removed and added.</summary>
    public static IReadOnlyList<string> Changes(PipelineSpec before, PipelineSpec after)
    {
        var changes = new List<string>();
        var old = Flatten(Without(before));
        var now = Flatten(Without(after));
        foreach (var (path, value) in now)
        {
            if (!old.TryGetValue(path, out var previous))
            {
                changes.Add($"{Label(path)}: {Show(value)}");
            }
            else if (previous != value)
            {
                changes.Add($"{Label(path)}: {Show(previous)} → {Show(value)}");
            }
        }

        var oldFeeds = string.Join(", ", before.Build.PackageFeeds.Select(Describe));
        var newFeeds = string.Join(", ", after.Build.PackageFeeds.Select(Describe));
        if (oldFeeds != newFeeds)
        {
            changes.Add($"Package feeds: {Show(oldFeeds)} → {Show(newFeeds)}");
        }

        var beforeEnvironments = before.Environments.ToDictionary(environment => environment.Name, StringComparer.Ordinal);
        var afterNames = after.Environments.Select(environment => environment.Name).ToHashSet(StringComparer.Ordinal);
        changes.AddRange(before.Environments.Where(environment => !afterNames.Contains(environment.Name)).Select(environment => $"Removed environment {environment.Name}"));
        foreach (var environment in after.Environments)
        {
            if (!beforeEnvironments.TryGetValue(environment.Name, out var previous))
            {
                changes.Add($"Added environment {environment.Name}");
                continue;
            }

            var oldFields = Flatten(JsonSerializer.SerializeToNode(previous, ProposalPrompt.Json)!);
            foreach (var (path, value) in Flatten(JsonSerializer.SerializeToNode(environment, ProposalPrompt.Json)!))
            {
                if (oldFields.GetValueOrDefault(path) != value)
                {
                    changes.Add($"Environment {environment.Name}, {Words(path)}: {Show(oldFields.GetValueOrDefault(path))} → {Show(value)}");
                }
            }
        }

        var order = string.Join(", ", after.Environments.Select(environment => environment.Name));
        if (afterNames.SetEquals(beforeEnvironments.Keys) && order != string.Join(", ", before.Environments.Select(environment => environment.Name)))
        {
            changes.Add($"Environment order: {order}");
        }

        return changes;
    }

    private static JsonObject Without(PipelineSpec spec)
    {
        var node = JsonSerializer.SerializeToNode(spec, ProposalPrompt.Json)!.AsObject();
        node.Remove("environments");
        node["build"]!.AsObject().Remove("packageFeeds");
        if (node["deploy"]!["publishFeed"] is JsonObject feed)
        {
            // One line for the feed instead of one per field.
            node["deploy"]!["publishFeed"] = $"{feed["kind"]} {feed["type"]} {feed["organization"]} {feed["project"]} {feed["feed"]} {feed["url"]}".Trim();
        }
        return node;
    }

    private static string Describe(PackageFeed feed) => $"{feed.Name} ({feed.Kind} {feed.Type}, {Feeds.Url(feed)})";

    /// <summary>path → text; lists of values become one comma-separated value, so a reordered list reads as one change.</summary>
    private static Dictionary<string, string?> Flatten(JsonNode node)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        void Walk(JsonNode? current, string path)
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                    {
                        Walk(child, path.Length == 0 ? key : $"{path}.{key}");
                    }

                    break;
                case JsonArray array:
                    values[path] = string.Join(", ", array.Select(item => item?.ToString()));
                    break;
                default:
                    values[path] = current?.ToString();
                    break;
            }
        }

        Walk(node, "");
        return values;
    }

    private static string Label(string path)
    {
        var parts = path.Split('.');
        return parts.Length == 1 ? Capital(Words(parts[0])) : $"{Capital(Words(parts[0]))}, {Words(parts[^1])}";
    }

    /// <summary>resourceName → resource name.</summary>
    private static string Words(string name)
    {
        var text = new StringBuilder();
        foreach (var character in name)
        {
            if (char.IsUpper(character) && text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(char.ToLowerInvariant(character));
        }

        return text.ToString();
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "(empty)" : value;

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> Lines(IEnumerable<string> values) => values.Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
}
