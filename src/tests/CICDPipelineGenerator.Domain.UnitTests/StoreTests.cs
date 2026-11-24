using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class StoreTests
{
    private static readonly string[] Paths = [GitHubGenerator.Path, AzureDevOpsGenerator.Path, GitLabGenerator.Path];
    private static readonly Platform[] All = GenerationTests.Six;

    private static string Content(GenerationResult result, string path) => result.Files.Single(file => file.Path == path).Content;

    private static GenerationResult Generate(PipelineSpec spec)
    {
        var result = PipelineGenerator.Generate(spec);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        GenerationTests.AssertActionlint(Content(result, GitHubGenerator.Path));
        return result;
    }

    [Fact]
    public void An_android_bundle_is_signed_uploaded_to_the_first_track_and_promoted_to_the_next()
    {
        var spec = GenerationTests.Spec(Target.GooglePlay, Stack.Android);
        var result = Generate(spec);
        foreach (var path in Paths)
        {
            var content = Content(result, path);
            Assert.Contains("sh ./gradlew --no-daemon test bundleRelease -Pandroid.injected.signing.store.file=\"$HOME/release.keystore\"", content, StringComparison.Ordinal);
            Assert.Contains("--package_name com.contoso.app --track internal --json_key \"$HOME/play.json\"", content, StringComparison.Ordinal);
            Assert.Contains("--track beta --track_promote_to production", content, StringComparison.Ordinal);
        }

        var github = Content(result, GitHubGenerator.Path);
        Assert.Contains("ANDROID_KEYSTORE: ${{ secrets.ANDROID_KEYSTORE }}", github, StringComparison.Ordinal);
        Assert.Contains("GOOGLE_PLAY_KEY: ${{ secrets.GOOGLE_PLAY_KEY }}", github, StringComparison.Ordinal);
        Assert.Contains("java-version: 17", github.Replace("'", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("image: ghcr.io/cirruslabs/android-sdk:35", Content(result, GitLabGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("$PIPELINE_WORKSPACE/app", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("## 1. Store credentials", Content(result, SetupGuide.Path), StringComparison.Ordinal);
        CoverageTests.AssertBash(All.SelectMany(platform => spec.Environments.Select(environment => Store.Deploy(spec, environment, platform))).Concat(Commands.Build(spec).Select(step => step.Script)));

        // Each branch uploads its own build.
        spec.Flow = Flow.BranchPerEnvironment;
        Assert.DoesNotContain("--track_promote_to", Content(Generate(spec), GitHubGenerator.Path), StringComparison.Ordinal);
        spec.Environments[0].ResourceName = null;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("write its Google Play track", StringComparison.Ordinal));
    }

    [Fact]
    public void An_ios_app_builds_and_uploads_on_macos()
    {
        var spec = GenerationTests.Spec(Target.AppStore, Stack.Ios);
        spec.Environments[0].ResourceName = null;
        var result = Generate(spec);
        var github = Content(result, GitHubGenerator.Path);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Count(github, "runs-on: macos-latest"));
        Assert.Contains("xcodebuild archive -workspace App.xcworkspace -scheme App -configuration Release -archivePath out/App.xcarchive", github, StringComparison.Ordinal);
        Assert.Contains("xcodebuild -exportArchive -archivePath out/App.xcarchive -exportOptionsPlist ExportOptions.plist -exportPath out/app", github, StringComparison.Ordinal);
        Assert.Contains("fastlane pilot upload --ipa", github, StringComparison.Ordinal);
        Assert.Contains("fastlane pilot distribute --app_identifier com.contoso.app --api_key_path \"$HOME/app-store.json\" --groups \"production\" --distribute_external true", github, StringComparison.Ordinal);
        Assert.Contains("IOS_CERTIFICATE: ${{ secrets.IOS_CERTIFICATE }}", github, StringComparison.Ordinal);
        Assert.DoesNotContain("uses: actions/setup-", github, StringComparison.Ordinal);

        Assert.Contains("vmImage: macOS-latest", Content(result, AzureDevOpsGenerator.Path), StringComparison.Ordinal);
        var gitlab = Content(result, GitLabGenerator.Path);
        Assert.Contains("- saas-macos-medium-m1", gitlab, StringComparison.Ordinal);
        Assert.Contains("image: macos-15-xcode-16", gitlab, StringComparison.Ordinal);
        CoverageTests.AssertBash(spec.Environments.Select(environment => Store.Deploy(spec, environment, Platform.GitLab)).Concat(Commands.Build(spec).Select(step => step.Script)));

        spec.Platforms = [Platform.Jenkins, Platform.CircleCi];
        var shell = PipelineGenerator.Generate(spec);
        Assert.False(shell.HasErrors);
        Assert.Contains("agent { label 'macos' }", Content(shell, JenkinsGenerator.Path), StringComparison.Ordinal);
        Assert.Contains("macos:", Content(shell, CircleCiGenerator.Path), StringComparison.Ordinal);
        SchemaTests.AssertValid(shell);

        spec.Platforms = [Platform.Bitbucket];
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.StartsWith("Bitbucket Pipelines has no hosted macOS", StringComparison.Ordinal));
        spec.Platforms = [Platform.GitHubActions];
        spec.Build.Agent = BuildAgent.HostedLinux;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.StartsWith("iOS builds with Xcode", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Stack.Android, MobileFramework.ReactNative, "sh ./gradlew --no-daemon bundleRelease", "npm ci")]
    [InlineData(Stack.Android, MobileFramework.Maui, "-f net10.0-android -c Release -p:AndroidPackageFormats=aab", "dotnet workload install maui-android")]
    [InlineData(Stack.Ios, MobileFramework.ReactNative, "xcodebuild archive -workspace App.xcworkspace", "(cd ios && pod install)")]
    [InlineData(Stack.Ios, MobileFramework.Maui, "-f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64", "dotnet workload install maui-ios")]
    public void React_native_and_maui_apps_build_for_the_same_stores(Stack stack, MobileFramework framework, string build, string first)
    {
        var spec = GenerationTests.Spec(stack == Stack.Android ? Target.GooglePlay : Target.AppStore, stack);
        spec.Build.MobileFramework = framework;
        if (framework == MobileFramework.Maui)
        {
            spec.Build.ProjectPath = "src/App/App.csproj";
        }

        var result = Generate(spec);
        foreach (var path in Paths)
        {
            var content = Content(result, path);
            Assert.Contains(build, content, StringComparison.Ordinal);
            Assert.Contains(first, content, StringComparison.Ordinal);
        }

        CoverageTests.AssertBash(Commands.Build(spec).Select(step => step.Script));
        if (stack == Stack.Ios && framework == MobileFramework.Maui)
        {
            Assert.Contains("IOS_CODESIGN_KEY: ${{ secrets.IOS_CODESIGN_KEY }}", Content(result, GitHubGenerator.Path), StringComparison.Ordinal);
        }

        spec.Build.Stack = Stack.DotNet;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.StartsWith("React Native and .NET MAUI are ways", StringComparison.Ordinal));
    }

    [Fact]
    public void An_msix_package_is_built_on_windows_and_published_to_a_flight()
    {
        var spec = GenerationTests.Spec(Target.MicrosoftStore, Stack.Msix);
        spec.Deploy.AppId = "9NBLGGH4R315";
        spec.Environments[0].ResourceName = "2e4b1f0a-1111-2222-3333-444455556666";
        spec.Environments[^1].ResourceName = null;
        var result = Generate(spec);
        foreach (var path in Paths)
        {
            var content = Content(result, path);
            Assert.Contains("-p:UapAppxPackageBuildMode=StoreUpload", content, StringComparison.Ordinal);
            Assert.Contains("msstore reconfigure --tenantId \"$MSSTORE_TENANT_ID\"", content, StringComparison.Ordinal);
            Assert.Contains("--appId 9NBLGGH4R315 --flightId 2e4b1f0a-1111-2222-3333-444455556666", content, StringComparison.Ordinal);
        }

        Assert.Contains("msstore publish src/App.Package --inputDirectory \"out/app\" --appId 9NBLGGH4R315\n", Content(result, GitHubGenerator.Path).Replace("\n          ", "\n", StringComparison.Ordinal) + "\n", StringComparison.Ordinal);
        ServerDeployTests.AssertParses(Commands.Build(spec).Select(step => step.Script));
        CoverageTests.AssertBash(spec.Environments.Select(environment => Store.Deploy(spec, environment, Platform.AzureDevOps)));

        spec.Build.Stack = Stack.DotNet;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.StartsWith("The Microsoft Store takes an MSIX build", StringComparison.Ordinal));
    }
}
