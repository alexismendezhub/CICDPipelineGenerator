namespace CICDPipelineGenerator.Domain;

/// <summary>
/// The checks a build can run next to its tests: the language's own formatter and linter, a Trivy scan of the
/// repository (packages with known vulnerabilities, secrets left in files, risky Dockerfile and infrastructure
/// settings), and a list of everything the build is made of (an SBOM). They are bash, for Linux build agents.
/// </summary>
public static class Quality
{
    /// <summary>The name CodeQL gives the language, and whether it reads the code without building it.</summary>
    public static (string Language, string BuildMode)? CodeQl(PipelineSpec spec) => spec.Build.CodeQl && !spec.IsInfrastructure ? spec.Build.Stack switch
    {
        Stack.DotNet => ("csharp", "none"),
        Stack.Node => ("javascript-typescript", "none"),
        Stack.Python => ("python", "none"),
        Stack.Java or Stack.Gradle or Stack.Android => ("java-kotlin", "none"),
        Stack.Ruby => ("ruby", "none"),
        Stack.Rust => ("rust", "none"),
        Stack.Go => ("go", "autobuild"),
        Stack.Cpp => ("c-cpp", "autobuild"),
        _ => null,
    } : null;

    public const string Reports = "out/reports";

    // Trivy's own installer puts the release for this machine in the home folder: no administrator rights.
    private const string InstallTrivy = "mkdir -p \"$HOME/.local/bin\"; export PATH=\"$HOME/.local/bin:$PATH\"\ncommand -v trivy > /dev/null || curl -sfL https://raw.githubusercontent.com/aquasecurity/trivy/main/contrib/install.sh | sh -s -- -b \"$HOME/.local/bin\" > /dev/null";

    public static bool Applies(PipelineSpec spec) => !spec.IsInfrastructure && !spec.IsWindowsBuild && !spec.IsMacBuild;

    public static bool CanLint(Stack stack) => stack is Stack.DotNet or Stack.Node or Stack.Python or Stack.Go or Stack.Rust or Stack.Ruby;

    /// <summary>Before the build: a problem found here stops the run before anything is compiled.</summary>
    public static IReadOnlyList<ScriptStep> Before(PipelineSpec spec)
    {
        var build = spec.Build;
        var steps = new List<ScriptStep>();
        if (!Applies(spec))
        {
            return steps;
        }

        var cd = build.WorkingDirectory is "." or "./" ? "" : $"cd {build.WorkingDirectory}\n";
        if (build.Lint && build.CustomCommands.Count == 0)
        {
            var target = string.IsNullOrWhiteSpace(build.TestPath) ? build.ProjectPath : build.TestPath;
            var lint = build.Stack switch
            {
                Stack.DotNet => $"dotnet format {target} --verify-no-changes",
                Stack.Node => cd + "npm ci\nnpm run lint --if-present",
                Stack.Python => cd + "pip install --quiet ruff\nruff check .",
                Stack.Go => cd + "unformatted=\"$(gofmt -l .)\"\nif [ -n \"$unformatted\" ]; then echo \"These files are not formatted with gofmt:\"; echo \"$unformatted\"; exit 1; fi",
                Stack.Rust => cd + "rustup component add rustfmt clippy > /dev/null\ncargo fmt --check\ncargo clippy --locked -- -D warnings",
                Stack.Ruby => cd + "bundle install --jobs 4\nbundle exec rubocop",
                _ => null,
            };
            if (lint is not null)
            {
                steps.Add(new("Check the code style", lint));
            }
        }

        if (build.CodeScan)
        {
            // Semgrep in its own Python environment; an image without Python gets it first.
            steps.Add(new("Analyse the code", "if ! python3 -m venv /tmp/semgrep 2> /dev/null; then apt-get update -qq && apt-get install -y -qq python3 python3-venv > /dev/null && python3 -m venv /tmp/semgrep; fi\n/tmp/semgrep/bin/pip install --quiet semgrep\n/tmp/semgrep/bin/semgrep scan --config auto --error --quiet ."));
        }

        if (build.SecurityScan)
        {
            steps.Add(new("Scan for vulnerabilities, secrets and risky settings", InstallTrivy + "\ntrivy fs --exit-code 1 --severity HIGH,CRITICAL --scanners vuln,secret,misconfig --no-progress ."));
        }

        return steps;
    }

    /// <summary>After the build: the list of what it is made of, kept with the run.</summary>
    public static IReadOnlyList<ScriptStep> After(PipelineSpec spec) => Applies(spec) && spec.Build.Sbom
        ? [new("Write the list of components (SBOM)", $"{InstallTrivy}\nmkdir -p {Reports}\ntrivy fs --format cyclonedx --output {Reports}/sbom.cdx.json --no-progress .")]
        : [];

    public const string CosignKey = "COSIGN_PRIVATE_KEY";
    public const string CosignPassword = "COSIGN_PASSWORD";

    /// <summary>Signs the pushed image with the key kept in a secret; the registry keeps the signature next to the image.</summary>
    public static string Sign(string image) => string.Join('\n',
    [
        "command -v cosign > /dev/null || { mkdir -p \"$HOME/.local/bin\"; { wget -qO \"$HOME/.local/bin/cosign\" https://github.com/sigstore/cosign/releases/latest/download/cosign-linux-amd64 || curl -fsSLo \"$HOME/.local/bin/cosign\" https://github.com/sigstore/cosign/releases/latest/download/cosign-linux-amd64; }; chmod +x \"$HOME/.local/bin/cosign\"; export PATH=\"$HOME/.local/bin:$PATH\"; }",
        $"cosign sign --yes --key env://{CosignKey} \"$(docker inspect --format '{{{{index .RepoDigests 0}}}}' \"{image}\")\"",
    ]);
}
