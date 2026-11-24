using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class PackageFeedTests
{
    public static TheoryData<FeedKind, FeedType, bool> Feeds()
    {
        var data = new TheoryData<FeedKind, FeedType, bool>();
        foreach (var kind in Enum.GetValues<FeedKind>())
        {
            foreach (var type in new[] { FeedType.NuGet, FeedType.Npm, FeedType.Maven, FeedType.Pip }.Where(type => !(kind == FeedKind.GitHubPackages && type == FeedType.Pip)))
            {
                data.Add(kind, type, false);
                data.Add(kind, type, true);
            }
        }

        return data;
    }

    [Theory]
    [InlineData(Stack.Gradle, FeedType.Maven, "ORG_GRADLE_PROJECT_companyUsername")]
    [InlineData(Stack.Go, FeedType.Go, "GOPROXY")]
    [InlineData(Stack.Ruby, FeedType.RubyGems, "BUNDLE_ACME__JFROG__IO")]
    [InlineData(Stack.Php, FeedType.Composer, "COMPOSER_AUTH")]
    [InlineData(Stack.Rust, FeedType.Cargo, "CARGO_REGISTRIES_COMPANY_TOKEN")]
    [InlineData(Stack.Scala, FeedType.Maven, "COURSIER_CREDENTIALS")]
    [InlineData(Stack.Elixir, FeedType.Hex, "mix hex.repo add company")]
    [InlineData(Stack.Flutter, FeedType.Pub, "dart pub token add")]
    [InlineData(Stack.Swift, FeedType.SwiftRegistry, "swift package-registry login")]
    [InlineData(Stack.Cpp, FeedType.Conan, "conan remote login company")]
    public void The_newer_languages_read_their_private_feeds_from_the_environment(Stack stack, FeedType type, string variable)
    {
        var spec = GenerationTests.Spec(Target.AppService, stack);
        spec.Platforms = [.. GenerationTests.Six];
        spec.Build.PackageFeeds = [new() { Name = "company", Type = type, Kind = FeedKind.JFrog, Url = "https://acme.jfrog.io", Feed = "libs" }];
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        foreach (var file in result.Files.Where(file => !file.Path.EndsWith(".md", StringComparison.Ordinal)))
        {
            Assert.Contains(variable, file.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("settings.xml", file.Content, StringComparison.Ordinal);
        }

        var github = result.Files.Single(file => file.Path == GitHubGenerator.Path).Content;
        GenerationTests.AssertActionlint(github);
        Assert.Contains("${{ secrets.FEED_COMPANY_TOKEN }}", github, StringComparison.Ordinal);
        var plan = ShellPlan.Create(spec, Platform.Bitbucket);
        CoverageTests.AssertBash(plan.Jobs[0].Steps.Select(step => Systems.WithEnv(step.Script, step.Env, powerShell: false)));

        spec.Build.PackageFeeds[0].Kind = FeedKind.AzureArtifacts;
        spec.Build.PackageFeeds[0].Organization = "contoso";
        if (type != FeedType.Maven)
        {
            Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("are written for JFrog Artifactory or another feed", StringComparison.Ordinal));
        }
    }

    internal static PackageFeed Feed(FeedKind kind, FeedType type, bool oidc = false) => new()
    {
        Name = "company",
        Type = type,
        Kind = kind,
        Organization = kind is FeedKind.AzureArtifacts or FeedKind.GitHubPackages ? "contoso" : null,
        Project = kind == FeedKind.AzureArtifacts ? "platform" : null,
        Feed = kind switch { FeedKind.AzureArtifacts => "packages", FeedKind.JFrog => "nuget-virtual", FeedKind.GitHubPackages when type == FeedType.Maven => "libraries", _ => null },
        Url = kind switch
        {
            FeedKind.JFrog => "https://acme.jfrog.io",
            FeedKind.Other => type switch
            {
                FeedType.NuGet => "https://packages.contoso.com/nuget/v3/index.json",
                FeedType.Npm => "https://packages.contoso.com/npm",
                FeedType.Maven => "https://packages.contoso.com/maven",
                _ => "https://packages.contoso.com/pypi/simple",
            },
            _ => null,
        },
        Scope = type == FeedType.Npm ? "@contoso" : null,
        JFrogOidcProvider = oidc && kind == FeedKind.JFrog ? "github" : null,
    };

    internal static PipelineSpec Spec(params PackageFeed[] feeds)
    {
        var stack = feeds.Select(feed => feed.Type).FirstOrDefault() switch
        {
            FeedType.Npm => Stack.Node,
            FeedType.Maven => Stack.Java,
            FeedType.Pip => Stack.Python,
            _ => Stack.DotNet,
        };
        var spec = GenerationTests.Spec(Target.AppService, stack);
        spec.Build.PackageFeeds = [.. feeds];
        return spec;
    }

    [Theory]
    [MemberData(nameof(Feeds))]
    public void Every_kind_of_feed_writes_valid_pipelines_for_every_system(FeedKind kind, FeedType type, bool oidc)
    {
        var feed = Feed(kind, type, oidc);
        var result = PipelineGenerator.Generate(Spec(feed));

        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        foreach (var file in result.Files.Where(file => file.Path.EndsWith(".yml", StringComparison.Ordinal)))
        {
            Assert.True(YamlWriterTests.Parse(file.Content).Children.Count > 0, file.Path);
            if (type != FeedType.Pip)
            {
                Assert.Contains("Configure package feeds", file.Content, StringComparison.Ordinal);
            }
        }

        // The feed address reaches every system (pip gets it with the credentials in front of the host).
        var url = PackageFeeds_Url(feed).Replace("https://", "", StringComparison.Ordinal);
        Assert.All(result.Files.Where(file => file.Path.EndsWith(".yml", StringComparison.Ordinal)), file => Assert.Contains(url, file.Content, StringComparison.Ordinal));
        Assert.Contains("## Package feeds", result.Files.Single(file => file.Path == SetupGuide.Path).Content, StringComparison.Ordinal);

        // actionlint reads the workflow the way GitHub does: expressions, step outputs and action inputs.
        GenerationTests.AssertActionlint(result.Files.Single(file => file.Path == GitHubGenerator.Path).Content);
        foreach (var used in Regex.Matches(result.Files.Single(file => file.Path == GitHubGenerator.Path).Content, @"uses: (\S+)").Select(match => match.Groups[1].Value))
        {
            Assert.Contains(used, GenerationTests.Node24OrDocker);
        }
    }

    private static string PackageFeeds_Url(PackageFeed feed) => Domain.Feeds.Url(feed);

    [Theory]
    [InlineData(FeedKind.AzureArtifacts, FeedType.NuGet, "https://pkgs.dev.azure.com/contoso/platform/_packaging/packages/nuget/v3/index.json")]
    [InlineData(FeedKind.AzureArtifacts, FeedType.Npm, "https://pkgs.dev.azure.com/contoso/platform/_packaging/packages/npm/registry/")]
    [InlineData(FeedKind.GitHubPackages, FeedType.NuGet, "https://nuget.pkg.github.com/contoso/index.json")]
    [InlineData(FeedKind.GitHubPackages, FeedType.Npm, "https://npm.pkg.github.com/")]
    [InlineData(FeedKind.JFrog, FeedType.NuGet, "https://acme.jfrog.io/artifactory/api/nuget/v3/nuget-virtual/index.json")]
    [InlineData(FeedKind.JFrog, FeedType.Npm, "https://acme.jfrog.io/artifactory/api/npm/nuget-virtual/")]
    [InlineData(FeedKind.Other, FeedType.NuGet, "https://packages.contoso.com/nuget/v3/index.json")]
    [InlineData(FeedKind.Other, FeedType.Npm, "https://packages.contoso.com/npm/")]
    public void Each_kind_of_feed_has_its_documented_address(FeedKind kind, FeedType type, string url) =>
        Assert.Equal(url, Domain.Feeds.Url(Feed(kind, type)));

    [Fact]
    public void An_organization_scoped_azure_artifacts_feed_has_no_project_in_its_address()
    {
        var feed = Feed(FeedKind.AzureArtifacts, FeedType.NuGet);
        feed.Project = null;
        Assert.Equal("https://pkgs.dev.azure.com/contoso/_packaging/packages/nuget/v3/index.json", Domain.Feeds.Url(feed));
    }

    [Fact]
    public void GitHub_passes_the_credentials_to_the_build_steps_only_never_to_the_whole_job()
    {
        var spec = Spec(Feed(FeedKind.AzureArtifacts, FeedType.NuGet));
        var content = GitHubGenerator.Generate(spec).Content;
        var build = Map(Map(YamlWriterTests.Parse(content), "jobs"), "build");
        Assert.False(build.Children.ContainsKey(new YamlScalarNode("env")));

        var steps = ((YamlSequenceNode)build.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToList();
        var configure = steps.Single(step => Name(step) == "Configure package feeds");
        Assert.Contains("dotnet nuget add source \"https://pkgs.dev.azure.com/contoso/platform/_packaging/packages/nuget/v3/index.json\" --name company", Text(configure, "run"), StringComparison.Ordinal);
        Assert.False(configure.Children.ContainsKey(new YamlScalarNode("env")));
        foreach (var name in new[] { "Restore", "Build", "Test", "Publish" })
        {
            var step = steps.Single(item => Name(item) == name);
            Assert.Equal("Username=azure-artifacts;Password=${{ secrets.FEED_COMPANY_TOKEN }}", Text(Map(step, "env"), "NuGetPackageSourceCredentials_company"));
        }

        // The configure step comes before the restore.
        Assert.True(steps.FindIndex(step => Name(step) == "Configure package feeds") < steps.FindIndex(step => Name(step) == "Restore"));
    }

    [Fact]
    public void GitHub_signs_in_to_jfrog_with_openid_connect_when_the_integration_is_given()
    {
        var content = GitHubGenerator.Generate(Spec(Feed(FeedKind.JFrog, FeedType.NuGet, oidc: true))).Content;

        Assert.Contains("uses: jfrog/setup-jfrog-cli@v5", content, StringComparison.Ordinal);
        Assert.Contains("id: jfrog_company", content, StringComparison.Ordinal);
        Assert.Contains("oidc-provider-name: github", content, StringComparison.Ordinal);
        Assert.Contains("JF_URL: https://acme.jfrog.io", content, StringComparison.Ordinal);
        Assert.Contains("Username=${{ steps.jfrog_company.outputs.oidc-user }};Password=${{ steps.jfrog_company.outputs.oidc-token }}", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.FEED_COMPANY_TOKEN", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_integration_jfrog_uses_an_access_token_everywhere()
    {
        var spec = Spec(Feed(FeedKind.JFrog, FeedType.NuGet));
        Assert.DoesNotContain("setup-jfrog-cli", GitHubGenerator.Generate(spec).Content, StringComparison.Ordinal);
        Assert.Contains("Username=${{ vars.FEED_COMPANY_USER }};Password=${{ secrets.FEED_COMPANY_TOKEN }}", GitHubGenerator.Generate(spec).Content, StringComparison.Ordinal);
        Assert.Contains("Username=$(FEED_COMPANY_USER);Password=$(FEED_COMPANY_TOKEN)", AzureDevOpsGenerator.Generate(spec).Content, StringComparison.Ordinal);
        Assert.Contains("Username=$FEED_COMPANY_USER;Password=$FEED_COMPANY_TOKEN", GitLabGenerator.Generate(spec).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHub_packages_are_read_with_the_workflow_token()
    {
        var content = GitHubGenerator.Generate(Spec(Feed(FeedKind.GitHubPackages, FeedType.NuGet))).Content;
        Assert.Equal("read", Text(Map(YamlWriterTests.Parse(content), "permissions"), "packages"));
        Assert.Contains("Username=${{ github.actor }};Password=${{ secrets.GITHUB_TOKEN }}", content, StringComparison.Ordinal);

        var without = GitHubGenerator.Generate(Spec(Feed(FeedKind.AzureArtifacts, FeedType.NuGet))).Content;
        Assert.False(Map(YamlWriterTests.Parse(without), "permissions").Children.ContainsKey(new YamlScalarNode("packages")));
    }

    [Fact]
    public void Azure_DevOps_signs_in_to_azure_artifacts_with_its_build_identity()
    {
        var spec = Spec(Feed(FeedKind.AzureArtifacts, FeedType.NuGet));
        var content = AzureDevOpsGenerator.Generate(spec).Content;
        Assert.Contains("task: NuGetAuthenticate@1", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGetPackageSourceCredentials_", content, StringComparison.Ordinal);
        Assert.DoesNotContain("FEED_COMPANY", content, StringComparison.Ordinal);

        var npm = AzureDevOpsGenerator.Generate(Spec(Feed(FeedKind.AzureArtifacts, FeedType.Npm))).Content;
        Assert.Contains("task: npmAuthenticate@0", npm, StringComparison.Ordinal);
        Assert.Contains("workingFile: web/.npmrc", npm, StringComparison.Ordinal);
        Assert.Contains("echo '@contoso:registry=https://pkgs.dev.azure.com/contoso/platform/_packaging/packages/npm/registry/' >> web/.npmrc", npm, StringComparison.Ordinal);
    }

    [Fact]
    public void Azure_DevOps_maps_the_secret_variables_into_each_build_step()
    {
        var content = AzureDevOpsGenerator.Generate(Spec(Feed(FeedKind.GitHubPackages, FeedType.Npm))).Content;
        // One mapping per build step: configure, install, build, test, package.
        Assert.Equal(5, Regex.Count(content, @"FEED_COMPANY_TOKEN: \$\(FEED_COMPANY_TOKEN\)"));
    }

    [Fact]
    public void GitLab_builds_the_nuget_credential_from_the_masked_variables()
    {
        var gitlab = YamlWriterTests.Parse(GitLabGenerator.Generate(Spec(Feed(FeedKind.Other, FeedType.NuGet))).Content);
        var build = Map(gitlab, "build");
        Assert.Equal("Username=$FEED_COMPANY_USER;Password=$FEED_COMPANY_TOKEN", Text(Map(build, "variables"), "NuGetPackageSourceCredentials_company"));
        var first = ((YamlScalarNode)((YamlSequenceNode)build.Children[new YamlScalarNode("script")]).Children[0]).Value!;
        Assert.StartsWith("# Configure package feeds", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Npm_reads_the_token_from_the_environment_and_never_stores_it()
    {
        var spec = Spec(Feed(FeedKind.JFrog, FeedType.Npm));
        var script = Domain.Feeds.ConfigureScript(spec, Platform.GitHubActions)!;
        Assert.Contains("npm config set \"@contoso:registry\" \"https://acme.jfrog.io/artifactory/api/npm/nuget-virtual/\"", script, StringComparison.Ordinal);
        Assert.Contains("npm config set \"//acme.jfrog.io/artifactory/api/npm/nuget-virtual/:_authToken\" '${FEED_COMPANY_TOKEN}'", script, StringComparison.Ordinal);

        // Azure Artifacts wants the token as a base64 password, computed from the variable at run time.
        var azure = Domain.Feeds.ConfigureScript(Spec(Feed(FeedKind.AzureArtifacts, FeedType.Npm)), Platform.GitLab)!;
        Assert.Contains(":_password\" \"$(printf '%s' \"$FEED_COMPANY_TOKEN\" | base64 | tr -d '\\n')\"", azure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_feed_already_in_the_repository_config_only_gets_its_credentials()
    {
        var feed = Feed(FeedKind.Other, FeedType.NuGet);
        feed.InRepositoryConfig = true;
        var spec = Spec(feed);
        Assert.Null(Domain.Feeds.ConfigureScript(spec, Platform.GitHubActions));
        var content = GitHubGenerator.Generate(spec).Content;
        Assert.DoesNotContain("dotnet nuget add source", content, StringComparison.Ordinal);
        Assert.Contains("NuGetPackageSourceCredentials_company", content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_setup_guide_says_which_secret_each_system_needs()
    {
        var jfrog = Feed(FeedKind.JFrog, FeedType.NuGet, oidc: true);
        jfrog.Name = "jfrog";
        var guide = SetupGuide.Generate(Spec(Feed(FeedKind.AzureArtifacts, FeedType.NuGet), jfrog)).Content;
        Assert.Contains("**Feed Reader**", guide, StringComparison.Ordinal);
        Assert.Contains("`FEED_COMPANY_TOKEN`", guide, StringComparison.Ordinal);
        Assert.Contains("Packaging (Read)", guide, StringComparison.Ordinal);
        Assert.Contains("OpenID Connect integration `github`", guide, StringComparison.Ordinal);
        Assert.Contains("`FEED_JFROG_TOKEN`", guide, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "the name uses letters")]
    [InlineData("my feed", "the name uses letters")]
    [InlineData("1feed", "the name uses letters")]
    public void Feed_names_are_usable_as_variable_names(string name, string message)
    {
        var feed = Feed(FeedKind.Other, FeedType.NuGet);
        feed.Name = name;
        Assert.Contains(PipelineRules.Check(Spec(feed)), item => item.Severity == Severity.Error && item.Text.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://feed.contoso.com/v3/index.json\"; rm -rf /")]
    [InlineData("http://feed.contoso.com/v3/index.json")]
    [InlineData("https://feed.contoso.com/$(whoami)")]
    [InlineData("https://feed contoso.com")]
    public void A_feed_url_that_could_break_a_command_is_refused(string url)
    {
        var feed = Feed(FeedKind.Other, FeedType.NuGet);
        feed.Url = url;
        Assert.Contains(PipelineRules.Check(Spec(feed)), item => item.Severity == Severity.Error && item.Text.Contains("feed URL", StringComparison.Ordinal));
    }

    [Fact]
    public void Feeds_must_match_the_build_and_their_kind()
    {
        var nugetOnNode = Feed(FeedKind.Other, FeedType.NuGet);
        var spec = GenerationTests.Spec(Target.AppService, Stack.Node);
        spec.Build.PackageFeeds = [nugetOnNode];
        Assert.Contains(PipelineRules.Check(spec), item => item.Text.Contains("NuGet feeds are for .NET builds", StringComparison.Ordinal));

        var github = Feed(FeedKind.GitHubPackages, FeedType.Npm);
        github.Scope = "@other";
        Assert.Contains(PipelineRules.Check(Spec(github)), item => item.Text.Contains("use the scope @contoso", StringComparison.Ordinal));

        var twice = Spec(Feed(FeedKind.Other, FeedType.NuGet), Feed(FeedKind.Other, FeedType.NuGet));
        Assert.Contains(PipelineRules.Check(twice), item => item.Text.Contains("appears twice", StringComparison.Ordinal));

        var terraform = GenerationTests.Spec(Target.Terraform, Stack.None);
        terraform.Build.PackageFeeds = [Feed(FeedKind.Other, FeedType.NuGet)];
        Assert.Contains(PipelineRules.Check(terraform), item => item.Severity == Severity.Error && item.Text.Contains("Terraform and Bicep", StringComparison.Ordinal));

        var container = GenerationTests.Spec(Target.ContainerApp, Stack.DotNet);
        container.Build.PackageFeeds = [Feed(FeedKind.Other, FeedType.NuGet)];
        Assert.Contains(PipelineRules.Check(container), item => item.Severity == Severity.Warning && item.Text.Contains("Dockerfile", StringComparison.Ordinal));
    }

    [Fact]
    public void The_ai_schema_lists_the_feed_fields_and_a_proposal_reports_feed_changes()
    {
        var feeds = (JsonObject)ProposalPrompt.Schema()["properties"]!["spec"]!["properties"]!["build"]!["properties"]!["packageFeeds"]!["items"]!;
        Assert.Equal(
            ["name", "type", "kind", "organization", "project", "feed", "url", "scope", "inRepositoryConfig", "jFrogOidcProvider"],
            ((JsonObject)feeds["properties"]!).Select(pair => pair.Key));
        Assert.Contains("jFrog", feeds["properties"]!["kind"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>()));

        var before = Spec(Feed(FeedKind.AzureArtifacts, FeedType.NuGet));
        var after = Spec(Feed(FeedKind.JFrog, FeedType.NuGet));
        Assert.Contains(ProposalBuilder.Changes(before, after), change => change.StartsWith("Package feeds:", StringComparison.Ordinal) && change.Contains("JFrog", StringComparison.Ordinal));
    }

    private static string? Name(YamlMappingNode step) =>
        step.Children.TryGetValue(new YamlScalarNode("name"), out var name) ? ((YamlScalarNode)name).Value : null;

    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node.Children[new YamlScalarNode(key)];

    private static string? Text(YamlMappingNode node, string key) => ((YamlScalarNode)node.Children[new YamlScalarNode(key)]).Value;
}
