namespace CICDPipelineGenerator.Domain;

/// <summary>The package manager a feed serves.</summary>
public enum FeedType
{
    NuGet,
    Npm,
    /// <summary>A Maven repository, for Java builds.</summary>
    Maven,
    /// <summary>A Python package index, for pip.</summary>
    Pip,
    /// <summary>A Go module proxy (GOPROXY).</summary>
    Go,
    /// <summary>A RubyGems source Bundler reads.</summary>
    RubyGems,
    /// <summary>A Composer repository (PHP).</summary>
    Composer,
    /// <summary>A Cargo registry (Rust), declared in .cargo/config.toml.</summary>
    Cargo,
    /// <summary>A Hex repository (Elixir).</summary>
    Hex,
    /// <summary>A pub repository (Dart and Flutter).</summary>
    Pub,
    /// <summary>A Swift package registry.</summary>
    SwiftRegistry,
    /// <summary>A Conan remote (C and C++).</summary>
    Conan,
}

/// <summary>Where the private packages are kept.</summary>
public enum FeedKind
{
    /// <summary>Azure DevOps Artifacts (organization- or project-scoped feed).</summary>
    AzureArtifacts,
    GitHubPackages,
    /// <summary>JFrog Artifactory (cloud or self-hosted).</summary>
    JFrog,
    /// <summary>Any other NuGet v3 feed or npm registry (ProGet, Nexus, MyGet...), given by its URL.</summary>
    Other,
}

/// <summary>A private feed the build restores packages from, besides nuget.org or npmjs.</summary>
public sealed class PackageFeed
{
    /// <summary>The source name (letters, digits and underscores); it also names the credential variables.</summary>
    public string Name { get; set; } = "";

    public FeedType Type { get; set; } = FeedType.NuGet;

    public FeedKind Kind { get; set; } = FeedKind.AzureArtifacts;

    /// <summary>Azure Artifacts: the Azure DevOps organization. GitHub Packages: the owner (user or organization).</summary>
    public string? Organization { get; set; }

    /// <summary>Azure Artifacts: the project of a project-scoped feed; empty for an organization-scoped feed.</summary>
    public string? Project { get; set; }

    /// <summary>Azure Artifacts: the feed name. JFrog: the repository key.</summary>
    public string? Feed { get; set; }

    /// <summary>JFrog: the platform address (https://acme.jfrog.io). Other: the full feed or registry URL.</summary>
    public string? Url { get; set; }

    /// <summary>npm: the scope of the private packages (@acme); empty sends every package to this registry.</summary>
    public string? Scope { get; set; }

    /// <summary>The repository's nuget.config or .npmrc already lists the feed; the pipeline only adds the credentials.</summary>
    public bool InRepositoryConfig { get; set; }

    /// <summary>JFrog on GitHub Actions: the OpenID Connect integration name in JFrog; empty signs in with an access token.</summary>
    public string? JFrogOidcProvider { get; set; }
}

/// <summary>The user name and password (or token) a pipeline system passes to a feed, as expressions of that system.</summary>
public sealed record FeedCredential(string User, string Token);

/// <summary>
/// What every pipeline system shares for private feeds: the feed URLs, the variable names and the shell commands that
/// register the feeds. Credentials never go in a file of the repository: NuGet reads them from the
/// NuGetPackageSourceCredentials_{name} variable and npm expands ${FEED_..._TOKEN} from the environment.
/// </summary>
public static class Feeds
{
    /// <summary>Azure Artifacts accepts any user name with a personal access token.</summary>
    public const string AzureArtifactsUser = "azure-artifacts";

    public static string Url(PackageFeed feed)
    {
        var url = (feed.Url ?? "").TrimEnd('/');
        return (feed.Kind, feed.Type) switch
        {
            (FeedKind.AzureArtifacts, FeedType.NuGet) => $"{AzureArtifactsBase(feed)}/nuget/v3/index.json",
            (FeedKind.AzureArtifacts, FeedType.Maven) => $"{AzureArtifactsBase(feed)}/maven/v1",
            (FeedKind.AzureArtifacts, FeedType.Pip) => $"{AzureArtifactsBase(feed)}/pypi/simple/",
            (FeedKind.GitHubPackages, FeedType.Maven) => $"https://maven.pkg.github.com/{feed.Organization}/{feed.Feed}",
            (FeedKind.JFrog, FeedType.Maven) => $"{url}/artifactory/{feed.Feed}",
            (FeedKind.JFrog, FeedType.Pip) => $"{url}/artifactory/api/pypi/{feed.Feed}/simple",
            (FeedKind.JFrog, FeedType.Go) => $"{url}/artifactory/api/go/{feed.Feed}",
            (FeedKind.JFrog, FeedType.RubyGems) => $"{url}/artifactory/api/gems/{feed.Feed}/",
            (FeedKind.JFrog, FeedType.Composer) => $"{url}/artifactory/api/composer/{feed.Feed}",
            (FeedKind.JFrog, FeedType.Cargo) => $"{url}/artifactory/api/cargo/{feed.Feed}",
            (FeedKind.GitHubPackages, FeedType.RubyGems) => $"https://rubygems.pkg.github.com/{feed.Organization}",
            (FeedKind.JFrog, FeedType.Pub) => $"{url}/artifactory/api/pub/{feed.Feed}",
            (FeedKind.JFrog, FeedType.SwiftRegistry) => $"{url}/artifactory/api/swift/{feed.Feed}",
            (FeedKind.JFrog, FeedType.Conan) => $"{url}/artifactory/api/conan/{feed.Feed}",
            (_, FeedType.Go or FeedType.RubyGems or FeedType.Composer or FeedType.Cargo or FeedType.Hex or FeedType.Pub or FeedType.SwiftRegistry or FeedType.Conan) => feed.Url ?? "",
            (FeedKind.AzureArtifacts, _) => $"{AzureArtifactsBase(feed)}/npm/registry/",
            (FeedKind.GitHubPackages, FeedType.NuGet) => $"https://nuget.pkg.github.com/{feed.Organization}/index.json",
            (FeedKind.GitHubPackages, _) => "https://npm.pkg.github.com/",
            (FeedKind.JFrog, FeedType.NuGet) => $"{url}/artifactory/api/nuget/v3/{feed.Feed}/index.json",
            (FeedKind.JFrog, _) => $"{url}/artifactory/api/npm/{feed.Feed}/",
            (_, FeedType.Npm) => url + "/",
            _ => feed.Url ?? "",
        };
    }

    /// <summary>The host of a feed's address, which Bundler and Composer key their credentials by.</summary>
    public static string Host(PackageFeed feed) => Uri.TryCreate(Url(feed), UriKind.Absolute, out var uri) ? uri.Host : "";

    private static string AzureArtifactsBase(PackageFeed feed) =>
        string.IsNullOrWhiteSpace(feed.Project)
            ? $"https://pkgs.dev.azure.com/{feed.Organization}/_packaging/{feed.Feed}"
            : $"https://pkgs.dev.azure.com/{feed.Organization}/{feed.Project}/_packaging/{feed.Feed}";

    /// <summary>FEED_COMPANY for the feed "company": FEED_COMPANY_USER and FEED_COMPANY_TOKEN hold its credentials.</summary>
    public static string Variable(PackageFeed feed) => "FEED_" + feed.Name.ToUpperInvariant();

    public static string UserVariable(PackageFeed feed) => Variable(feed) + "_USER";

    public static string TokenVariable(PackageFeed feed) => Variable(feed) + "_TOKEN";

    /// <summary>The variable NuGet reads the credentials of the source with this name from.</summary>
    public static string NuGetCredentialVariable(PackageFeed feed) => "NuGetPackageSourceCredentials_" + feed.Name;

    public static string NuGetCredentialValue(FeedCredential credential) => $"Username={credential.User};Password={credential.Token}";

    /// <summary>Azure DevOps signs in to Azure Artifacts feeds of its organization with its own build identity (no secret).</summary>
    public static bool UsesBuildIdentity(PackageFeed feed, Platform platform) =>
        platform == Platform.AzureDevOps && feed.Kind == FeedKind.AzureArtifacts && feed.Type is FeedType.NuGet or FeedType.Npm;

    public static string JFrogStep(PackageFeed feed) => "jfrog_" + feed.Name;

    /// <summary>The user and the password or token of a feed, as each system writes a variable or a secret.</summary>
    public static FeedCredential Credential(PackageFeed feed, Platform platform) => platform switch
    {
        Platform.GitHubActions => feed.Kind switch
        {
            FeedKind.GitHubPackages => new("${{ github.actor }}", "${{ secrets.GITHUB_TOKEN }}"),
            FeedKind.JFrog when UsesJFrogOidc(feed, platform) =>
                new($"${{{{ steps.{JFrogStep(feed)}.outputs.oidc-user }}}}", $"${{{{ steps.{JFrogStep(feed)}.outputs.oidc-token }}}}"),
            FeedKind.AzureArtifacts => new(AzureArtifactsUser, $"${{{{ secrets.{TokenVariable(feed)} }}}}"),
            _ => new($"${{{{ vars.{UserVariable(feed)} }}}}", $"${{{{ secrets.{TokenVariable(feed)} }}}}"),
        },
        // The build identity's own token reads the organization's Azure Artifacts feeds.
        Platform.AzureDevOps => feed.Kind == FeedKind.AzureArtifacts
            ? new(AzureArtifactsUser, "$(System.AccessToken)")
            : new($"$({UserVariable(feed)})", $"$({TokenVariable(feed)})"),
        _ => new(feed.Kind == FeedKind.AzureArtifacts ? AzureArtifactsUser : "$" + UserVariable(feed), "$" + TokenVariable(feed)),
    };

    /// <summary>
    /// The variables the build steps need to read the feeds: one per NuGet source, the token of each npm registry,
    /// the user and token Maven's settings.xml refers to, and pip's extra indexes with their credentials.
    /// forConfigure is the step that only registers the feeds, which needs the npm tokens alone.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> BuildEnv(PipelineSpec spec, Platform platform, bool forConfigure = false)
    {
        var env = new List<KeyValuePair<string, string>>();
        var pip = new List<string>();
        var go = new List<string>();
        var composer = new List<string>();
        var coursier = new List<string>();
        var gradle = spec.Build.Stack is Stack.Gradle or Stack.Android;
        foreach (var feed in spec.Build.PackageFeeds.Where(feed => !UsesBuildIdentity(feed, platform)))
        {
            var credential = Credential(feed, platform);
            switch (feed.Type)
            {
                case FeedType.NuGet when !forConfigure:
                    env.Add(new(NuGetCredentialVariable(feed), NuGetCredentialValue(credential)));
                    break;
                case FeedType.Npm:
                    env.Add(new(TokenVariable(feed), credential.Token));
                    break;
                case FeedType.Maven when !forConfigure && gradle:
                    // Gradle fills the credentials of a repository named like the feed from these two project properties.
                    env.Add(new($"ORG_GRADLE_PROJECT_{feed.Name}Username", credential.User));
                    env.Add(new($"ORG_GRADLE_PROJECT_{feed.Name}Password", credential.Token));
                    break;
                case FeedType.Maven when !forConfigure && spec.Build.Stack == Stack.Scala:
                    // sbt downloads with Coursier, which reads a line per host: host user:password.
                    coursier.Add($"{Host(feed)} {credential.User}:{credential.Token}");
                    break;
                case FeedType.Hex or FeedType.Pub or FeedType.SwiftRegistry:
                    // The step that registers the feed signs in to it, so it gets the token too.
                    env.Add(new(TokenVariable(feed), credential.Token));
                    break;
                case FeedType.Conan:
                    env.Add(new(UserVariable(feed), credential.User));
                    env.Add(new(TokenVariable(feed), credential.Token));
                    break;
                case FeedType.Go when !forConfigure:
                    go.Add(Url(feed).Replace("https://", $"https://{credential.User}:{credential.Token}@", StringComparison.Ordinal));
                    break;
                case FeedType.RubyGems when !forConfigure:
                    // Bundler reads the credentials of a host from BUNDLE_<HOST>, with __ for a dot and ___ for a dash.
                    env.Add(new("BUNDLE_" + Host(feed).ToUpperInvariant().Replace("-", "___", StringComparison.Ordinal).Replace(".", "__", StringComparison.Ordinal), $"{credential.User}:{credential.Token}"));
                    break;
                case FeedType.Composer when !forConfigure:
                    composer.Add($"\"{Host(feed)}\":{{\"username\":\"{credential.User}\",\"password\":\"{credential.Token}\"}}");
                    break;
                case FeedType.Cargo when !forConfigure:
                    env.Add(new($"CARGO_REGISTRIES_{feed.Name.ToUpperInvariant()}_TOKEN", credential.Token));
                    break;
                case FeedType.Maven when !forConfigure:
                    env.Add(new(UserVariable(feed), credential.User));
                    env.Add(new(TokenVariable(feed), credential.Token));
                    break;
                case FeedType.Pip when !forConfigure:
                    pip.Add(Url(feed).Replace("https://", $"https://{credential.User}:{credential.Token}@", StringComparison.Ordinal));
                    break;
            }
        }

        if (pip.Count > 0)
        {
            env.Add(new("PIP_EXTRA_INDEX_URL", string.Join(' ', pip)));
        }

        if (go.Count > 0)
        {
            // The private proxies first, then the public one and the source itself.
            env.Add(new("GOPROXY", string.Join(',', go) + ",https://proxy.golang.org,direct"));
        }

        if (coursier.Count > 0)
        {
            env.Add(new("COURSIER_CREDENTIALS", string.Join('\n', coursier)));
        }

        if (composer.Count > 0)
        {
            env.Add(new("COMPOSER_AUTH", "{\"http-basic\":{" + string.Join(',', composer) + "}}"));
        }

        if (!forConfigure)
        {
            // What signs an Android bundle or an iOS archive.
            env.AddRange(Store.SigningSecrets(spec).Select(name => new KeyValuePair<string, string>(name, Secrets.Shared(platform, name))));
        }

        if (!forConfigure && !string.IsNullOrWhiteSpace(spec.Build.SonarProjectKey))
        {
            // Where SonarQube is and the token it accepts: a variable and a secret of the pipeline system.
            env.Add(new("SONAR_HOST_URL", platform switch { Platform.GitHubActions => "${{ vars.SONAR_HOST_URL }}", Platform.AzureDevOps => "$(SONAR_HOST_URL)", _ => "$SONAR_HOST_URL" }));
            env.Add(new("SONAR_TOKEN", platform switch { Platform.GitHubActions => "${{ secrets.SONAR_TOKEN }}", Platform.AzureDevOps => "$(SONAR_TOKEN)", _ => "$SONAR_TOKEN" }));
        }

        return env;
    }

    /// <summary>The feeds as secret build arguments of the image build; the Dockerfile declares the ARGs it uses.</summary>
    public static string DockerArguments(PipelineSpec spec) =>
        string.Concat(spec.Build.PackageFeeds.Select(feed => $" --secret-build-arg {UserVariable(feed)}=\"${UserVariable(feed)}\" --secret-build-arg {TokenVariable(feed)}=\"${TokenVariable(feed)}\""));

    public static IReadOnlyList<KeyValuePair<string, string>> DockerEnv(PipelineSpec spec, Platform platform) =>
        spec.Build.PackageFeeds.SelectMany(feed =>
        {
            var credential = Credential(feed, platform);
            return new KeyValuePair<string, string>[] { new(UserVariable(feed), credential.User), new(TokenVariable(feed), credential.Token) };
        }).ToList();

    /// <summary>Maven reads the feeds from a settings.xml written when the build runs; the credentials stay in the environment.</summary>
    private static string MavenSettings(IReadOnlyList<PackageFeed> feeds)
    {
        var lines = new List<string> { "mkdir -p \"$HOME/.m2\"", "cat > \"$HOME/.m2/settings.xml\" <<'XML'", "<settings>", "  <servers>" };
        lines.AddRange(feeds.Select(feed => $"    <server><id>{feed.Name}</id><username>${{env.{UserVariable(feed)}}}</username><password>${{env.{TokenVariable(feed)}}}</password></server>"));
        lines.Add("  </servers>");
        var added = feeds.Where(feed => !feed.InRepositoryConfig).ToList();
        if (added.Count > 0)
        {
            lines.Add("  <profiles><profile><id>feeds</id><repositories>");
            lines.AddRange(added.Select(feed => $"    <repository><id>{feed.Name}</id><url>{Url(feed)}</url></repository>"));
            lines.Add("  </repositories></profile></profiles>");
            lines.Add("  <activeProfiles><activeProfile>feeds</activeProfile></activeProfiles>");
        }

        lines.Add("</settings>");
        lines.Add("XML");
        return string.Join('\n', lines);
    }

    /// <summary>GitHub Actions signs in to JFrog with OpenID Connect when the integration name is given.</summary>
    public static bool UsesJFrogOidc(PackageFeed feed, Platform platform) =>
        platform == Platform.GitHubActions && feed.Kind == FeedKind.JFrog && !string.IsNullOrWhiteSpace(feed.JFrogOidcProvider);

    /// <summary>The address npm keys credentials by: the registry URL without the scheme (//host/path/).</summary>
    public static string NpmAuthKey(PackageFeed feed)
    {
        var url = Url(feed);
        var start = url.IndexOf("//", StringComparison.Ordinal);
        return start < 0 ? url : url[start..];
    }

    /// <summary>The npmrc line that points the scope (or every package) at the feed.</summary>
    public static string NpmRegistryLine(PackageFeed feed) =>
        string.IsNullOrWhiteSpace(feed.Scope) ? $"registry={Url(feed)}" : $"{feed.Scope}:registry={Url(feed)}";

    /// <summary>
    /// The bash commands of the "Configure package feeds" step, run from the repository root before the restore.
    /// The credentials arrive as environment variables of the step (see the generators); nothing secret is written
    /// into the repository, and the npm token is read from the environment each time npm runs.
    /// </summary>
    public static string? ConfigureScript(PipelineSpec spec, Platform platform)
    {
        var feeds = spec.Build.PackageFeeds;
        if (feeds.Count == 0)
        {
            return null;
        }

        var lines = new List<string>();
        var maven = feeds.Where(feed => feed.Type == FeedType.Maven && spec.Build.Stack == Stack.Java).ToList();
        if (maven.Count > 0)
        {
            lines.Add(MavenSettings(maven));
        }

        var npmrc = Commands.Join(spec.Build.WorkingDirectory, ".npmrc");
        foreach (var feed in feeds.Where(feed => feed.Type is FeedType.NuGet or FeedType.Npm))
        {
            if (feed.Type == FeedType.NuGet)
            {
                if (!feed.InRepositoryConfig)
                {
                    lines.Add($"dotnet nuget add source \"{Url(feed)}\" --name {feed.Name}");
                }

                continue;
            }

            if (UsesBuildIdentity(feed, platform))
            {
                // npmAuthenticate adds the build identity's token to this .npmrc, so the registry must be listed in it.
                if (!feed.InRepositoryConfig)
                {
                    lines.Add($"echo '{NpmRegistryLine(feed)}' >> {npmrc}");
                }

                continue;
            }

            if (!feed.InRepositoryConfig)
            {
                lines.Add(string.IsNullOrWhiteSpace(feed.Scope)
                    ? $"npm config set registry \"{Url(feed)}\""
                    : $"npm config set \"{feed.Scope}:registry\" \"{Url(feed)}\"");
            }

            var key = NpmAuthKey(feed);
            if (feed.Kind == FeedKind.AzureArtifacts)
            {
                // Azure Artifacts takes the token as a base64 password, which npm cannot compute from a variable.
                lines.Add($"npm config set \"{key}:username\" {AzureArtifactsUser}");
                lines.Add($"npm config set \"{key}:_password\" \"$(printf '%s' \"${TokenVariable(feed)}\" | base64 | tr -d '\\n')\"");
                lines.Add($"npm config set \"{key}:email\" npm-requires-an-email@example.com");
            }
            else
            {
                lines.Add($"npm config set \"{key}:_authToken\" '${{{TokenVariable(feed)}}}'");
            }
        }

        foreach (var feed in feeds.Where(feed => !UsesBuildIdentity(feed, platform)))
        {
            var url = Url(feed);
            switch (feed.Type)
            {
                case FeedType.Maven when spec.Build.Stack == Stack.Scala && !feed.InRepositoryConfig:
                    lines.Add($"mkdir -p \"$HOME/.sbt/1.0\"\necho 'resolvers += \"{feed.Name}\" at \"{url}\"' >> \"$HOME/.sbt/1.0/feeds.sbt\"");
                    break;
                case FeedType.Hex:
                    lines.Add($"mix local.hex --force > /dev/null\nmix hex.repo add {feed.Name} \"{url}\" --auth-key \"${TokenVariable(feed)}\"");
                    break;
                case FeedType.Pub:
                    // pub keeps the name of the variable, and reads the token from it when it downloads.
                    lines.Add($"dart pub token add \"{url}\" --env-var {TokenVariable(feed)}");
                    break;
                case FeedType.SwiftRegistry:
                    lines.Add((feed.InRepositoryConfig ? "" : $"swift package-registry set --global \"{url}\"\n") + $"swift package-registry login \"{url}\" --token \"${TokenVariable(feed)}\" --no-confirm");
                    break;
                case FeedType.Conan:
                    lines.Add(ConanInstall + $"\nconan remote add {feed.Name} \"{url}\" --force\nconan remote login {feed.Name} \"${UserVariable(feed)}\" -p \"${TokenVariable(feed)}\"");
                    break;
            }
        }

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>Conan itself, with the compiler it finds as its profile.</summary>
    public const string ConanInstall = "command -v conan > /dev/null || { command -v pip3 > /dev/null || { apt-get update -qq && apt-get install -y -qq python3-pip > /dev/null; }; pip3 install --quiet --break-system-packages conan; }\nconan profile detect --force > /dev/null";

    public static bool UsesConan(PipelineSpec spec) => spec.Build.Stack == Stack.Cpp && spec.Build.PackageFeeds.Any(feed => feed.Type == FeedType.Conan);

    public static string TypeLabel(FeedType type) => type switch
    {
        FeedType.NuGet => "NuGet feed",
        FeedType.Npm => "npm registry",
        FeedType.Maven => "Maven repository",
        FeedType.Pip => "Python package index",
        FeedType.Go => "Go module proxy",
        FeedType.RubyGems => "RubyGems source",
        FeedType.Composer => "Composer repository",
        FeedType.Cargo => "Cargo registry",
        FeedType.Hex => "Hex repository",
        FeedType.Pub => "pub repository",
        FeedType.SwiftRegistry => "Swift package registry",
        _ => "Conan remote",
    };
}
