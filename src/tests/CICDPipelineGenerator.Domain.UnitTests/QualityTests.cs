using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class QualityTests
{
    private static readonly Platform[] All = GenerationTests.Six;

    [Theory]
    [InlineData(Stack.DotNet, "dotnet format MyApp.slnx --verify-no-changes")]
    [InlineData(Stack.Node, "npm run lint --if-present")]
    [InlineData(Stack.Python, "ruff check .")]
    [InlineData(Stack.Go, "gofmt -l .")]
    [InlineData(Stack.Rust, "cargo clippy --locked -- -D warnings")]
    [InlineData(Stack.Ruby, "bundle exec rubocop")]
    public void The_style_check_the_scan_and_the_sbom_run_in_every_system(Stack stack, string lint)
    {
        var spec = GenerationTests.Spec(Target.AppService, stack);
        spec.Platforms = [.. All];
        spec.Build.Lint = spec.Build.SecurityScan = spec.Build.Sbom = true;
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        foreach (var file in result.Files.Where(file => !file.Path.EndsWith(".md", StringComparison.Ordinal)))
        {
            Assert.Contains(lint, file.Content, StringComparison.Ordinal);
            Assert.Contains("trivy fs --exit-code 1 --severity HIGH,CRITICAL --scanners vuln,secret,misconfig --no-progress .", file.Content, StringComparison.Ordinal);
            Assert.Contains("trivy fs --format cyclonedx --output out/reports/sbom.cdx.json --no-progress .", file.Content, StringComparison.Ordinal);
            Assert.True(file.Content.IndexOf("--exit-code 1", StringComparison.Ordinal) < file.Content.IndexOf("--format cyclonedx", StringComparison.Ordinal), file.Path);
            Assert.Contains("out/reports", file.Content.Replace("out/reports/sbom.cdx.json", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }

        GenerationTests.AssertActionlint(result.Files.Single(file => file.Path == GitHubGenerator.Path).Content);
        CoverageTests.AssertBash(Commands.Build(spec).Select(step => step.Script));
    }

    [Fact]
    public void The_checks_are_refused_where_they_cannot_run_and_an_image_is_signed_after_the_push()
    {
        var windows = GenerationTests.Spec(Target.AppService, Stack.DotNet);
        windows.Build.Agent = BuildAgent.HostedWindows;
        windows.Build.SecurityScan = true;
        Assert.Contains(PipelineRules.Check(windows), message => message.Text.StartsWith("The code style check, the security scan", StringComparison.Ordinal));
        var java = GenerationTests.Spec(Target.AppService, Stack.Java);
        java.Build.Lint = true;
        Assert.Contains(PipelineRules.Check(java), message => message.Text.StartsWith("The code style check is for", StringComparison.Ordinal));

        var spec = GenerationTests.Spec(Target.ContainerRegistry, Stack.DotNet);
        spec.Platforms = [.. All];
        spec.Deploy.SignImage = true;
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        foreach (var file in result.Files.Where(file => !file.Path.EndsWith(".md", StringComparison.Ordinal)))
        {
            Assert.Contains("cosign sign --yes --key env://COSIGN_PRIVATE_KEY \"$(docker inspect --format '{{index .RepoDigests 0}}'", file.Content.Replace("\\'", "'", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.True(file.Content.IndexOf("docker push", StringComparison.Ordinal) < file.Content.IndexOf("cosign sign", StringComparison.Ordinal), file.Path);
        }

        Assert.Contains("COSIGN_PRIVATE_KEY: ${{ secrets.COSIGN_PRIVATE_KEY }}", result.Files.Single(file => file.Path == GitHubGenerator.Path).Content, StringComparison.Ordinal);
        Assert.Contains("COSIGN_PASSWORD = credentials('COSIGN_PASSWORD')", result.Files.Single(file => file.Path == JenkinsGenerator.Path).Content, StringComparison.Ordinal);
        CoverageTests.AssertBash([CloudDeploy.BuildImage(spec, "abc")]);

        spec.Deploy.Target = Target.ContainerApp;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.StartsWith("Signing the image is for", StringComparison.Ordinal));
    }
}
