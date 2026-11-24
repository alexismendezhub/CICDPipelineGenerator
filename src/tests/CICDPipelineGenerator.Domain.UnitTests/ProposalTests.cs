using System.Text.Json.Nodes;
using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class ProposalTests
{
    // What the AI answers: every field present, "" where nothing is set.
    private const string Answer = """
        {
          "summary": "A .NET API to App Service with GitHub Actions.",
          "spec": {
            "name": "orders",
            "repository": "",
            "platforms": ["gitHubActions", "gitHubActions"],
            "build": { "stack": "dotNet", "version": "", "workingDirectory": "", "projectPath": " src/Orders/Orders.csproj ", "testPath": "", "runTests": true, "outputFolder": "", "customCommands": ["", " "] },
            "deploy": {
              "target": "appService", "os": "linux", "registry": "", "imageName": "", "dockerfile": "", "dockerContext": "", "namespace": "", "manifests": "",
              "deploymentName": "", "containerName": "", "entraIdCluster": true, "infraDirectory": "", "terraformVersion": "", "stateResourceGroup": "",
              "stateStorageAccount": "", "stateContainer": "", "bicepTemplate": "", "preview": true
            },
            "flow": "branchPerEnvironment",
            "mainBranch": "",
            "buildPullRequests": true,
            "manualRun": true,
            "pathFilters": [],
            "environments": [
              { "name": "dev", "branch": "develop", "requireApproval": false, "resourceGroup": "rg-orders-dev", "resourceName": "app-orders-dev", "slot": "", "serviceConnection": "", "parametersFile": "" },
              { "name": "prod", "branch": "", "requireApproval": true, "resourceGroup": "rg-orders-prod", "resourceName": "app-orders-prod", "slot": "", "serviceConnection": "", "parametersFile": "" }
            ]
          },
          "review": [{ "area": "environments", "text": "The resource names are proposals." }, { "area": "build", "text": " " }],
          "questions": ["Which repository?", ""]
        }
        """;

    [Fact]
    public void An_answer_becomes_settings_with_defaults_for_what_was_left_empty()
    {
        var proposal = ProposalBuilder.Build(ProposalPrompt.Parse(Answer), baseline: null);
        var spec = proposal.Spec;

        Assert.Equal("A .NET API to App Service with GitHub Actions.", proposal.Summary);
        Assert.Equal([Platform.GitHubActions], spec.Platforms);
        Assert.Null(spec.Repository);
        Assert.Equal("main", spec.MainBranch);
        Assert.Equal("src/Orders/Orders.csproj", spec.Build.ProjectPath);
        Assert.Null(spec.Build.TestPath);
        Assert.Equal(".", spec.Build.WorkingDirectory);
        Assert.Empty(spec.Build.CustomCommands);
        Assert.Equal("1.9.8", spec.Deploy.TerraformVersion);
        Assert.Null(spec.Environments[0].Slot);
        Assert.Equal("main", spec.Environments[1].Branch);
        Assert.Equal([new ReviewItem(ReviewArea.Environments, "The resource names are proposals.")], proposal.Review);
        Assert.Equal(["Which repository?"], proposal.Questions);
        Assert.Empty(proposal.Changes);

        // The proposal is checked like any settings, and it generates.
        Assert.DoesNotContain(proposal.Messages, message => message.Severity == Severity.Error);
        Assert.False(PipelineGenerator.Generate(spec).HasErrors);
    }

    [Fact]
    public void Changes_compare_the_proposal_with_the_settings_before_it()
    {
        var before = ProposalBuilder.Build(ProposalPrompt.Parse(Answer), null).Spec;
        var after = ProposalBuilder.Build(ProposalPrompt.Parse(Answer), null).Spec;
        after.Platforms = [Platform.GitHubActions, Platform.GitLab];
        after.Deploy.Target = Target.FunctionApp;
        after.Environments[1].RequireApproval = false;
        after.Environments.Insert(1, new EnvironmentSpec { Name = "test", Branch = "release" });
        before.Environments.Add(new EnvironmentSpec { Name = "old" });

        var changes = ProposalBuilder.Changes(before, after);

        Assert.Contains("Platforms: gitHubActions → gitHubActions, gitLab", changes);
        Assert.Contains("Deploy, target: appService → functionApp", changes);
        Assert.Contains("Environment prod, require approval: true → false", changes);
        Assert.Contains("Added environment test", changes);
        Assert.Contains("Removed environment old", changes);
        Assert.Equal(5, changes.Count);
    }

    [Fact]
    public void Reordered_environments_are_one_change()
    {
        var before = GenerationTests.Spec(Target.AppService);
        var after = GenerationTests.Spec(Target.AppService);
        after.Environments.Reverse();

        Assert.Equal(["Environment order: prod, test, dev"], ProposalBuilder.Changes(before, after));
    }

    [Fact]
    public void The_schema_is_closed_and_requires_every_field()
    {
        var objects = 0;
        void Walk(JsonNode? node)
        {
            if (node is not JsonObject obj)
            {
                return;
            }

            if (obj["type"]?.GetValue<string>() == "object")
            {
                objects++;
                var properties = obj["properties"]!.AsObject();
                Assert.False(obj["additionalProperties"]!.GetValue<bool>());
                Assert.Equal(properties.Select(pair => pair.Key), obj["required"]!.AsArray().Select(item => item!.GetValue<string>()));
                foreach (var (_, child) in properties)
                {
                    Walk(child);
                }
            }

            Walk(obj["items"]);
        }

        var schema = ProposalPrompt.Schema();
        Walk(schema);

        // The answer, spec, build, package feed, deploy, the feed to publish to, environment, setting and review item.
        Assert.Equal(9, objects);
        var target = schema["properties"]!["spec"]!["properties"]!["deploy"]!["properties"]!["target"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>());
        Assert.Equal(Enum.GetNames<Target>().Select(name => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(name)), target);
    }

    [Fact]
    public void The_prompt_sends_the_current_settings_only_when_changing_them()
    {
        Assert.Contains("There are no current settings", ProposalPrompt.User("a site", null));
        Assert.Contains("\"target\":\"appService\"", ProposalPrompt.User("add prod", GenerationTests.Spec(Target.AppService)));
        Assert.Contains("gitHubActions, azureDevOps, gitLab", ProposalPrompt.System());
    }
}
