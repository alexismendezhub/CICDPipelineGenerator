using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class LanguageTests
{
    [Theory]
    [InlineData(Stack.Gradle, "$gradle --no-daemon build", "cp build/libs/*.jar out/app/", "gradle:jdk21", "cache: gradle", "JavaToolInstaller@0")]
    [InlineData(Stack.Go, "go test ./...", "CGO_ENABLED=0 go build -o \"$root/out/app/\" ./...", "golang:1.25", "uses: actions/setup-go@v6", "GoTool@0")]
    [InlineData(Stack.Ruby, "bundle exec rake", "--exclude=./vendor/bundle", "ruby:3.4", "uses: ruby/setup-ruby@v1", "UseRubyVersion@0")]
    [InlineData(Stack.Php, "vendor/bin/phpunit", "composer install --no-interaction --prefer-dist --no-dev --optimize-autoloader", "php:8.4-cli", "uses: shivammathur/setup-php@v2", "update-alternatives --set php /usr/bin/php8.4")]
    [InlineData(Stack.Cpp, "ctest --test-dir build --output-on-failure", "cmake --install build --prefix \"$root/out/app\"", "gcc:14", "cmake --build build --parallel", "cmake -S . -B build")]
    [InlineData(Stack.Swift, "swift test", "find .build/release -maxdepth 1 -type f -perm -u+x", "swift:6.0", "swift build -c release", "swift build -c release")]
    [InlineData(Stack.Scala, "sbt -batch test package", "-path '*/scala-*/*.jar'", "eclipse-temurin:21", "cache: sbt", "JavaToolInstaller@0")]
    [InlineData(Stack.Elixir, "mix test", "MIX_ENV=prod mix release --overwrite --path \"$root/out/app\"", "elixir:1.18", "uses: erlef/setup-beam@v1", "apt-get install -y -qq elixir erlang")]
    [InlineData(Stack.Flutter, "flutter test", "cp -r build/web/. out/app/", "ghcr.io/cirruslabs/flutter:stable", "uses: subosito/flutter-action@v2", "git clone --quiet --depth 1 --branch stable https://github.com/flutter/flutter.git")]
    [InlineData(Stack.Rust, "cargo test --release --locked", "find target/release -maxdepth 1 -type f -perm -u+x", "rust:latest", "rustup toolchain install stable", "rustup default stable")]
    public void Every_language_builds_tests_and_packages_on_the_three_systems(Stack stack, string test, string package, string image, string github, string azure)
    {
        var spec = GenerationTests.Spec(Target.AppService, stack);
        spec.Build.AuditDependencies = stack is Stack.Go or Stack.Ruby or Stack.Php or Stack.Rust;
        var result = PipelineGenerator.Generate(spec);

        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        SchemaTests.AssertValid(result);
        var workflow = result.Files.Single(file => file.Path == GitHubGenerator.Path).Content;
        GenerationTests.AssertActionlint(workflow);
        CoverageTests.AssertBash(Commands.Build(spec).Select(step => step.Script));
        foreach (var file in result.Files.Where(file => file.Path.EndsWith(".yml", StringComparison.Ordinal)))
        {
            Assert.Contains(test, file.Content, StringComparison.Ordinal);
            Assert.Contains(package, file.Content, StringComparison.Ordinal);
        }

        Assert.Contains(github, workflow, StringComparison.Ordinal);
        Assert.Contains(azure, result.Files.Single(file => file.Path == AzureDevOpsGenerator.Path).Content, StringComparison.Ordinal);
        Assert.Contains("image: " + image, result.Files.Single(file => file.Path == GitLabGenerator.Path).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Php_on_a_plain_image_installs_composer_first_and_a_java_app_service_gets_the_gradle_jar()
    {
        var gitlab = GitLabGenerator.Generate(GenerationTests.Spec(Target.AppService, Stack.Php)).Content;
        Assert.Contains("getcomposer.org/installer", gitlab, StringComparison.Ordinal);
        Assert.True(gitlab.IndexOf("getcomposer.org", StringComparison.Ordinal) < gitlab.IndexOf("composer install", StringComparison.Ordinal));

        var gradle = GenerationTests.Spec(Target.AppService, Stack.Gradle);
        Assert.Contains("--src-path out/app/*.jar --type jar", GitLabGenerator.Generate(gradle).Content, StringComparison.Ordinal);
        gradle.Build.PublishTestResults = true;
        Assert.Contains("**/build/test-results/test/TEST-*.xml", AzureDevOpsGenerator.Generate(gradle).Content, StringComparison.Ordinal);

        var rust = GenerationTests.Spec(Target.AppService, Stack.Rust);
        rust.Build.Version = "1.83";
        Assert.DoesNotContain(PipelineRules.Check(rust), message => message.Severity == Severity.Error);
        rust.Build.Agent = BuildAgent.HostedWindows;
        Assert.Contains(PipelineRules.Check(rust), message => message.Text.Contains("build on Linux agents", StringComparison.Ordinal));
    }
}
