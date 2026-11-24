namespace CICDPipelineGenerator.Domain;

/// <summary>Where each language keeps its downloaded packages, so the pipeline systems can keep them between runs.</summary>
public static class PackageCache
{
    public static bool Supports(Stack stack) => stack is Stack.DotNet or Stack.Node or Stack.Python or Stack.Java or Stack.Gradle or Stack.Go or Stack.Rust or Stack.Php or Stack.Scala or Stack.Flutter;

    public static bool Applies(PipelineSpec spec) =>
        spec.Build.CachePackages && !spec.IsInfrastructure && spec.Build.CustomCommands.Count == 0
        && spec.Build.Agent == BuildAgent.HostedLinux && Supports(spec.Build.Stack);

    /// <summary>The folder, from the home folder (where each tool keeps it) or from the root the variable below points the tool at.</summary>
    public static string Folder(Stack stack) => stack switch
    {
        Stack.DotNet => ".nuget/packages",
        Stack.Node => ".npm",
        Stack.Python => ".cache/pip",
        Stack.Gradle => ".gradle/caches",
        Stack.Go => "go/pkg/mod",
        Stack.Rust => ".cargo/registry",
        Stack.Php => ".cache/composer",
        Stack.Scala => ".cache/coursier",
        Stack.Flutter => ".pub-cache",
        _ => ".m2/repository",
    };

    /// <summary>The variable that points the tool at that folder.</summary>
    public static KeyValuePair<string, string> Variable(Stack stack, string root) => stack switch
    {
        Stack.DotNet => new("NUGET_PACKAGES", $"{root}/{Folder(stack)}"),
        Stack.Node => new("npm_config_cache", $"{root}/{Folder(stack)}"),
        Stack.Python => new("PIP_CACHE_DIR", $"{root}/{Folder(stack)}"),
        Stack.Gradle => new("GRADLE_USER_HOME", $"{root}/.gradle"),
        Stack.Go => new("GOMODCACHE", $"{root}/{Folder(stack)}"),
        Stack.Rust => new("CARGO_HOME", $"{root}/.cargo"),
        Stack.Php => new("COMPOSER_CACHE_DIR", $"{root}/{Folder(stack)}"),
        Stack.Scala => new("COURSIER_CACHE", $"{root}/{Folder(stack)}"),
        Stack.Flutter => new("PUB_CACHE", $"{root}/{Folder(stack)}"),
        _ => new("MAVEN_OPTS", $"-Dmaven.repo.local={root}/{Folder(stack)}"),
    };

    /// <summary>The files whose change means different packages.</summary>
    public static string KeyFiles(PipelineSpec spec) => spec.Build.Stack switch
    {
        Stack.DotNet => "**/*.csproj",
        Stack.Node => Commands.Join(spec.Build.WorkingDirectory, "package-lock.json"),
        Stack.Python => Commands.Join(spec.Build.WorkingDirectory, "requirements.txt"),
        Stack.Gradle => "**/*.gradle*",
        Stack.Go => Commands.Join(spec.Build.WorkingDirectory, "go.sum"),
        Stack.Rust => Commands.Join(spec.Build.WorkingDirectory, "Cargo.lock"),
        Stack.Php => Commands.Join(spec.Build.WorkingDirectory, "composer.lock"),
        Stack.Scala => "**/build.sbt",
        Stack.Flutter => Commands.Join(spec.Build.WorkingDirectory, "pubspec.lock"),
        _ => "**/pom.xml",
    };

    public static string Name(Stack stack) => stack switch
    {
        Stack.DotNet => "nuget",
        Stack.Node => "npm",
        Stack.Python => "pip",
        Stack.Java => "maven",
        _ => stack.ToString().ToLowerInvariant(),
    };
}
