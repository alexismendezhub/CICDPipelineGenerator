namespace CICDPipelineGenerator.Domain;

/// <summary>
/// Publishing to the application stores with their own tools: fastlane for Google Play and TestFlight, and the
/// Microsoft Store Developer CLI. The credentials are secrets of the pipeline system, the same for every environment.
/// </summary>
public static class Store
{
    public const string PlayKey = "GOOGLE_PLAY_KEY";
    public static readonly string[] AndroidSigning = ["ANDROID_KEYSTORE", "ANDROID_KEYSTORE_PASSWORD", "ANDROID_KEY_ALIAS", "ANDROID_KEY_PASSWORD"];
    public static readonly string[] IosSigning = ["IOS_CERTIFICATE", "IOS_CERTIFICATE_PASSWORD", "IOS_PROVISIONING_PROFILE"];
    public static readonly string[] AppStoreKey = ["APP_STORE_KEY_ID", "APP_STORE_ISSUER_ID", "APP_STORE_KEY"];
    public static readonly string[] MicrosoftStore = ["MSSTORE_TENANT_ID", "MSSTORE_SELLER_ID", "MSSTORE_CLIENT_ID", "MSSTORE_CLIENT_SECRET"];

    // fastlane in the user's own gem folder, so it installs without administrator rights on every runner.
    private const string Fastlane = "command -v fastlane > /dev/null || { gem install --user-install fastlane --no-document; export PATH=\"$(ruby -e 'puts Gem.user_dir')/bin:$PATH\"; }";
    private const string SkipListing = "--skip_upload_metadata true --skip_upload_images true --skip_upload_screenshots true --skip_upload_changelogs true";

    /// <summary>The secrets the build needs to sign what it builds.</summary>
    public static IReadOnlyList<string> SigningSecrets(PipelineSpec spec) => spec.Build.CustomCommands.Count > 0 ? [] : spec.Build.Stack switch
    {
        Stack.Android => AndroidSigning,
        Stack.Ios when spec.Build.MobileFramework == MobileFramework.Maui => [.. IosSigning, "IOS_CODESIGN_KEY", "IOS_PROVISIONING_PROFILE_NAME"],
        Stack.Ios => IosSigning,
        _ => [],
    };

    /// <summary>The secrets the deploy of a store target reads.</summary>
    public static IReadOnlyList<string> Secrets(PipelineSpec spec) => spec.Deploy.Target switch
    {
        Target.GooglePlay => [PlayKey],
        Target.AppStore => AppStoreKey,
        Target.MicrosoftStore => MicrosoftStore,
        _ => [],
    };

    public static IReadOnlyList<KeyValuePair<string, string>> Env(PipelineSpec spec, Platform platform) =>
        Secrets(spec).Select(name => new KeyValuePair<string, string>(name, Domain.Secrets.Shared(platform, name))).ToList();

    /// <summary>The first environment uploads the build; with one branch or a tag, the next ones move that same build on.</summary>
    public static string Deploy(PipelineSpec spec, EnvironmentSpec environment, Platform platform)
    {
        var deploy = spec.Deploy;
        var package = platform == Platform.AzureDevOps ? "$PIPELINE_WORKSPACE/app" : Commands.PackageFolder;
        var index = spec.Environments.IndexOf(environment);
        var previous = spec.Promotes && index > 0 ? spec.Environments[index - 1] : null;
        switch (deploy.Target)
        {
            case Target.GooglePlay:
            {
                const string Key = "printf '%s' \"$GOOGLE_PLAY_KEY\" > \"$HOME/play.json\"";
                return previous is null
                    ? $"{Fastlane}\n{Key}\nfastlane supply --aab \"$(ls \"{package}\"/*.aab | head -1)\" --package_name {deploy.AppId} --track {environment.ResourceName} --json_key \"$HOME/play.json\" {SkipListing}"
                    : $"{Fastlane}\n{Key}\nfastlane supply --package_name {deploy.AppId} --track {previous.ResourceName} --track_promote_to {environment.ResourceName} --json_key \"$HOME/play.json\" --skip_upload_aab true --skip_upload_apk true {SkipListing}";
            }

            case Target.AppStore:
            {
                // The App Store Connect API key as the JSON file fastlane reads.
                const string Key = "python3 -c \"import json, os; json.dump({'key_id': os.environ['APP_STORE_KEY_ID'], 'issuer_id': os.environ['APP_STORE_ISSUER_ID'], 'key': os.environ['APP_STORE_KEY'], 'in_house': False}, open(os.path.expanduser('~/app-store.json'), 'w'))\"";
                var groups = string.IsNullOrWhiteSpace(environment.ResourceName) ? "" : $" --groups \"{environment.ResourceName}\" --distribute_external true";
                // After the last environment the same build goes to App Store review; releasing it stays a decision in App Store Connect.
                var review = deploy.SubmitForReview && index == spec.Environments.Count - 1
                    ? $"\nfastlane deliver --app_identifier {deploy.AppId} --api_key_path \"$HOME/app-store.json\" --submit_for_review true --automatic_release false --force true --skip_binary_upload true --skip_metadata true --skip_screenshots true --run_precheck_before_submit false"
                    : "";
                return (previous is null
                    ? $"{Fastlane}\n{Key}\nfastlane pilot upload --ipa \"$(ls \"{package}\"/*.ipa | head -1)\" --api_key_path \"$HOME/app-store.json\"{(groups.Length > 0 ? groups : " --skip_waiting_for_build_processing true")}"
                    : $"{Fastlane}\n{Key}\nfastlane pilot distribute --app_identifier {deploy.AppId} --api_key_path \"$HOME/app-store.json\"{groups}") + review;
            }

            default:
            {
                var flight = string.IsNullOrWhiteSpace(environment.ResourceName) ? "" : $" --flightId {environment.ResourceName}";
                return string.Join('\n',
                [
                    "mkdir -p \"$HOME/.msstore\"; export PATH=\"$HOME/.msstore:$PATH\"",
                    "command -v msstore > /dev/null || curl -fsSL https://github.com/microsoft/msstore-cli/releases/latest/download/MSStoreCLI-linux-x64.tar.gz | tar -xz -C \"$HOME/.msstore\"",
                    "msstore reconfigure --tenantId \"$MSSTORE_TENANT_ID\" --sellerId \"$MSSTORE_SELLER_ID\" --clientId \"$MSSTORE_CLIENT_ID\" --clientSecret \"$MSSTORE_CLIENT_SECRET\"",
                    $"msstore publish {Folder(spec.Build.ProjectPath)} --inputDirectory \"{package}\" --appId {deploy.AppId}{flight}",
                ]);
            }
        }
    }

    private static string Folder(string? project)
    {
        var path = project ?? ".";
        var slash = path.LastIndexOf('/');
        return path.EndsWith("proj", StringComparison.OrdinalIgnoreCase) ? (slash < 0 ? "." : path[..slash]) : path;
    }

    /// <summary>Android: the keystore kept base64-encoded in a secret, handed to Gradle's own signing properties.</summary>
    public static IReadOnlyList<ScriptStep> AndroidBuild(PipelineSpec spec, string cd)
    {
        var build = spec.Build;
        const string Keystore = "printf '%s' \"$ANDROID_KEYSTORE\" | base64 -d > \"$HOME/release.keystore\"\n";
        if (build.MobileFramework == MobileFramework.Maui)
        {
            var framework = $"net{BuildSettings.DefaultVersion(Stack.DotNet)}-android";
            return
            [
                new("Install the MAUI workload", "dotnet workload install maui-android"),
                new("Publish the bundle", Keystore + $"dotnet publish {build.ProjectPath} -f {framework} -c Release -p:AndroidPackageFormats=aab -p:AndroidKeyStore=true -p:AndroidSigningKeyStore=\"$HOME/release.keystore\" -p:AndroidSigningStorePass=\"$ANDROID_KEYSTORE_PASSWORD\" -p:AndroidSigningKeyAlias=\"$ANDROID_KEY_ALIAS\" -p:AndroidSigningKeyPass=\"$ANDROID_KEY_PASSWORD\" -o out/maui"),
                new("Package", $"mkdir -p {Commands.PackageFolder}\ncp out/maui/*-Signed.aab {Commands.PackageFolder}/"),
            ];
        }

        if (build.MobileFramework == MobileFramework.ReactNative)
        {
            // The native project of a React Native app is in its android folder.
            var android = Commands.Join(build.WorkingDirectory, "android");
            return
            [
                new("Install the JavaScript packages", cd + "npm ci" + (build.RunTests ? "\nnpm test --if-present" : "")),
                new("Build the bundle", Keystore + $"cd {android}\nsh ./gradlew --no-daemon bundleRelease -Pandroid.injected.signing.store.file=\"$HOME/release.keystore\" -Pandroid.injected.signing.store.password=\"$ANDROID_KEYSTORE_PASSWORD\" -Pandroid.injected.signing.key.alias=\"$ANDROID_KEY_ALIAS\" -Pandroid.injected.signing.key.password=\"$ANDROID_KEY_PASSWORD\""),
                new("Package", $"mkdir -p {Commands.PackageFolder}\nfind {android} -path '*/build/outputs/bundle/release/*.aab' -exec cp {{}} {Commands.PackageFolder}/ \\;"),
            ];
        }

        const string Signing = " -Pandroid.injected.signing.store.file=\"$HOME/release.keystore\" -Pandroid.injected.signing.store.password=\"$ANDROID_KEYSTORE_PASSWORD\" -Pandroid.injected.signing.key.alias=\"$ANDROID_KEY_ALIAS\" -Pandroid.injected.signing.key.password=\"$ANDROID_KEY_PASSWORD\"";
        return
        [
            new(build.RunTests ? "Test and build the bundle" : "Build the bundle", "printf '%s' \"$ANDROID_KEYSTORE\" | base64 -d > \"$HOME/release.keystore\"\n" + cd + $"sh ./gradlew --no-daemon {(build.RunTests ? "test " : "")}bundleRelease{Signing}"),
            new("Package", $"mkdir -p {Commands.PackageFolder}\nfind {build.WorkingDirectory} -path '*/build/outputs/bundle/release/*.aab' -exec cp {{}} {Commands.PackageFolder}/ \\;"),
        ];
    }

    /// <summary>iOS on a macOS agent: the certificate and profile go into a keychain of their own; xcodebuild archives and exports the .ipa.</summary>
    public static IReadOnlyList<ScriptStep> IosBuild(PipelineSpec spec)
    {
        var build = spec.Build;
        var project = (build.ProjectPath ?? "").EndsWith(".xcworkspace", StringComparison.OrdinalIgnoreCase) ? $"-workspace {build.ProjectPath}" : $"-project {build.ProjectPath}";
        var steps = new List<ScriptStep>
        {
            new("Set up signing", string.Join('\n',
            [
                "keychain=\"$HOME/Library/Keychains/build.keychain-db\"",
                "password=\"$(uuidgen)\"",
                "printf '%s' \"$IOS_CERTIFICATE\" | base64 -d > \"$HOME/certificate.p12\"",
                "security create-keychain -p \"$password\" \"$keychain\"",
                "security set-keychain-settings -lut 21600 \"$keychain\"",
                "security unlock-keychain -p \"$password\" \"$keychain\"",
                "security import \"$HOME/certificate.p12\" -P \"$IOS_CERTIFICATE_PASSWORD\" -A -t cert -f pkcs12 -k \"$keychain\"",
                "security set-key-partition-list -S apple-tool:,apple: -k \"$password\" \"$keychain\" > /dev/null",
                "security list-keychains -d user -s \"$keychain\"",
                "mkdir -p \"$HOME/Library/MobileDevice/Provisioning Profiles\"",
                "printf '%s' \"$IOS_PROVISIONING_PROFILE\" | base64 -d > \"$HOME/Library/MobileDevice/Provisioning Profiles/build.mobileprovision\"",
            ])),
        };
        if (build.MobileFramework == MobileFramework.Maui)
        {
            var framework = $"net{BuildSettings.DefaultVersion(Stack.DotNet)}-ios";
            steps.Insert(0, new("Install the MAUI workload", "dotnet workload install maui-ios"));
            steps.Add(new("Publish the app", $"dotnet publish {build.ProjectPath} -f {framework} -c Release -p:RuntimeIdentifier=ios-arm64 -p:ArchiveOnBuild=true -p:CodesignKey=\"$IOS_CODESIGN_KEY\" -p:CodesignProvision=\"$IOS_PROVISIONING_PROFILE_NAME\" -p:IpaPackageDir=\"$PWD/{Commands.PackageFolder}\""));
            return steps;
        }

        if (build.MobileFramework == MobileFramework.ReactNative)
        {
            steps.Insert(0, new("Install the JavaScript packages and the pods", "npm ci" + (build.RunTests ? "\nnpm test --if-present" : "") + "\n(cd ios && pod install)"));
        }

        if (build.RunTests && build.MobileFramework == MobileFramework.Native)
        {
            // The first iPhone simulator this Xcode has.
            steps.Add(new("Test", $"simulator=\"$(xcrun simctl list devices available | grep -m1 -o 'iPhone [^(]*' | sed 's/ *$//')\"\nxcodebuild test {project} -scheme {build.Scheme} -destination \"platform=iOS Simulator,name=$simulator\""));
        }

        steps.Add(new("Archive", $"xcodebuild archive {project} -scheme {build.Scheme} -configuration Release -archivePath out/App.xcarchive"));
        steps.Add(new("Export", $"xcodebuild -exportArchive -archivePath out/App.xcarchive -exportOptionsPlist {build.ExportOptions} -exportPath {Commands.PackageFolder}"));
        return steps;
    }
}
