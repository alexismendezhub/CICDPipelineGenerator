using System.Globalization;
using System.Text.Json.Nodes;
using CICDPipelineGenerator.Domain;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

/// <summary>
/// Checks azure-pipelines.yml and .gitlab-ci.yml against the JSON schemas Microsoft and GitLab publish for their
/// editors: keywords, where each one may be, the type of every value, and (Azure DevOps) the inputs of each task.
/// </summary>
public class SchemaTests
{
    private static readonly Lazy<JsonSchema> AzureDevOps = new(() => Load("azure-pipelines.json"));
    private static readonly Lazy<JsonSchema> GitLab = new(() => Load("gitlab-ci.json"));
    private static readonly Lazy<JsonSchema> CircleCi = new(LoadCircleCi);

    private static JsonSchema LoadCircleCi()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Schemas", "circleci.json")))!;

        // Some definitions refer to themselves (a step that holds steps), which the validator reads as a circle. Inside
        // a definition, a reference to itself accepts anything: the generated files have no steps inside steps.
        foreach (var (name, definition) in schema["definitions"]!.AsObject().ToList())
        {
            CutSelfReferences(definition!, "#/definitions/" + name);
        }

        return JsonSchema.FromText(schema.ToJsonString());
    }

    private static void CutSelfReferences(JsonNode node, string self)
    {
        switch (node)
        {
            case JsonObject map:
                foreach (var (key, value) in map.ToList())
                {
                    if (value is JsonObject child && child["$ref"]?.GetValue<string>() == self)
                    {
                        map[key] = new JsonObject();
                    }
                    else if (value is not null)
                    {
                        CutSelfReferences(value, self);
                    }
                }

                break;
            case JsonArray list:
                for (var index = 0; index < list.Count; index++)
                {
                    if (list[index] is JsonObject child && child["$ref"]?.GetValue<string>() == self)
                    {
                        list[index] = new JsonObject();
                    }
                    else if (list[index] is { } item)
                    {
                        CutSelfReferences(item, self);
                    }
                }

                break;
        }
    }

    // Not in the repository (it has no licence): checked when someone downloaded it next to the others.
    private static readonly Lazy<JsonSchema?> Bitbucket = new(() => File.Exists(Path.Combine(AppContext.BaseDirectory, "Schemas", "bitbucket-pipelines.json")) ? Load("bitbucket-pipelines.json") : null);

    private static JsonSchema Load(string name) =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Schemas", name)));

    private static readonly Lock Gate = new();

    internal static void AssertValid(GenerationResult result)
    {
        foreach (var file in result.Files)
        {
            if (file.Path.Contains("azure-pipelines", StringComparison.Ordinal))
            {
                AssertValid(AzureDevOps.Value, file);
            }
            else if (file.Path.EndsWith("gitlab-ci.yml", StringComparison.Ordinal))
            {
                AssertValid(GitLab.Value, file);
            }
            else if (file.Path == CircleCiGenerator.Path || file.Path == CircleCiGenerator.ContinuePath)
            {
                AssertValid(CircleCi.Value, file);
            }
            else if (file.Path == BitbucketGenerator.Path && Bitbucket.Value is { } bitbucket)
            {
                AssertValid(bitbucket, file);
            }
        }
    }

    private static void AssertValid(JsonSchema schema, GeneratedFile file)
    {
        var json = ToJson(YamlWriterTests.Parse(file.Content));

        // The Azure DevOps schema describes the booleans of a schedule as text ("true"), although the file says true.
        if (json?["schedules"] is JsonArray schedules)
        {
            foreach (var schedule in schedules.OfType<JsonObject>().Where(item => item["always"] is JsonValue))
            {
                schedule["always"] = schedule["always"]!.ToString().ToLowerInvariant();
            }
        }

        // The schema library keeps what it has resolved in shared tables: tests running at once take turns.
        EvaluationResults evaluation;
        lock (Gate)
        {
            evaluation = schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });
        }

        if (evaluation.IsValid)
        {
            return;
        }

        // The deepest places say what is wrong; the ones above them only say that something below did not match.
        var errors = evaluation.Details
            .Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => (Location: detail.InstanceLocation.ToString(), Text: $"SCHEMA {detail.InstanceLocation}: {error.Value}")))
            .Distinct()
            .OrderByDescending(error => error.Location.Count(character => character == '/'))
            .Take(12)
            .Select(error => error.Text);
        Assert.Fail($"{file.Path} does not follow its schema:\n{string.Join("\n", errors)}");
    }

    /// <summary>YAML as the pipeline systems read it: plain true, false, null and numbers are not text.</summary>
    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(pair => KeyValuePair.Create(((YamlScalarNode)pair.Key).Value!, ToJson(pair.Value)))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ToJson).ToArray()),
        YamlScalarNode { Style: YamlDotNet.Core.ScalarStyle.Plain } scalar => scalar.Value switch
        {
            "true" => true,
            "false" => false,
            null or "" or "~" or "null" => null,
            var text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            var text => text,
        },
        YamlScalarNode scalar => scalar.Value,
        _ => null,
    };

    [Theory]
    [MemberData(nameof(GenerationTests.Combinations), MemberType = typeof(GenerationTests))]
    public void Every_combination_follows_the_azure_devops_and_gitlab_schemas(Target target, Stack stack, Flow flow)
    {
        var spec = GenerationTests.Spec(target, stack, flow);
        var environment = spec.Environments[^1];
        environment.BeforeDeploy = [spec.UsesPowerShell ? "Write-Host 'before'" : "echo before"];
        environment.AfterDeploy = [spec.UsesPowerShell ? "Write-Host 'after'" : "echo after"];
        AssertValid(PipelineGenerator.Generate(spec));
    }

    [Theory]
    [MemberData(nameof(PackageFeedTests.Feeds), MemberType = typeof(PackageFeedTests))]
    public void Every_kind_of_feed_follows_the_schemas(FeedKind kind, FeedType type, bool oidc) =>
        AssertValid(PipelineGenerator.Generate(PackageFeedTests.Spec(PackageFeedTests.Feed(kind, type, oidc))));

    [Fact]
    public void A_wrong_file_is_caught()
    {
        var spec = GenerationTests.Spec(Target.AppService);
        var azure = AzureDevOpsGenerator.Generate(spec);
        var gitlab = GitLabGenerator.Generate(spec);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertValid(AzureDevOps.Value, azure with { Content = azure.Content.Replace("displayName: Build and test", "displayNam: Build and test", StringComparison.Ordinal) }));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertValid(GitLab.Value, gitlab with { Content = gitlab.Content.Replace("stage: build", "stage: [1]", StringComparison.Ordinal) }));
    }
}
