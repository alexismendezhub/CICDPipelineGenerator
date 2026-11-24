namespace CICDPipelineGenerator.Domain;

/// <summary>A named shell step; every system runs it with bash (or sh in the GitLab Terraform image).</summary>
public sealed record ScriptStep(string Name, string Script);

/// <summary>
/// The shell commands every pipeline system shares, so GitHub, Azure DevOps and GitLab build and deploy the same way.
/// Commands start at the repository root; what is deployed is left in out/app.
/// </summary>
public static class Commands
{
    public const string PackageFolder = "out/app";
    public const string TestResults = "out/test-results";

    // NU1903 and NU1904 are the known high and critical vulnerabilities; as errors they stop the restore.
    private const string NuGetAudit = " -p:NuGetAudit=true -p:NuGetAuditMode=all \"-warnaserror:NU1903;NU1904\"";

    /// <summary>The build and test steps. For containers only the tests run here; the image is built in Azure Container Registry.</summary>
    public static IReadOnlyList<ScriptStep> Build(PipelineSpec spec) => [.. Quality.Before(spec), .. Compile(spec), .. Quality.After(spec)];

    private static List<ScriptStep> Compile(PipelineSpec spec)
    {
        var build = spec.Build;
        if (spec.IsInfrastructure)
        {
            return [];
        }

        if (build.CustomCommands.Count > 0)
        {
            return [new("Build", string.Join('\n', build.CustomCommands.Select(command => command.Trim())))];
        }

        var package = spec.UsesPackage;
        var steps = new List<ScriptStep>();
        if (build.Stack == Stack.DotNetFramework)
        {
            return Framework(spec);
        }

        if (build.Stack == Stack.Msix)
        {
            // The Store signs the package itself, so the upload package is built unsigned.
            var find = FindMsBuild.Replace("\r\n", "\n");
            return
            [
                new("Build the package", $"{find}$out = (Join-Path (Get-Location) 'out\\app') -replace '\\\\', '/'\n& $msbuild {build.ProjectPath} -restore -p:Configuration=Release -p:Platform=x64 -p:UapAppxPackageBuildMode=StoreUpload -p:AppxBundle=Always -p:AppxBundlePlatforms=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false \"-p:AppxPackageDir=$out/\" -nologo\nif ($LASTEXITCODE -ne 0) {{ throw 'The package build failed.' }}"),
            ];
        }

        var cd = build.WorkingDirectory is "." or "./" ? "" : $"cd {build.WorkingDirectory}\n";
        switch (build.Stack)
        {
            case Stack.DotNet:
            {
                var project = build.ProjectPath ?? "";
                var target = string.IsNullOrWhiteSpace(build.TestPath) ? project : build.TestPath;
                var sonar = !string.IsNullOrWhiteSpace(build.SonarProjectKey);
                const string Tools = "export PATH=\"$PATH:$HOME/.dotnet/tools\"\n";
                if (sonar)
                {
                    // The analysis wraps the build: begin, build and test, end.
                    steps.Add(new("Start the SonarQube analysis", $"dotnet tool update --global dotnet-sonarscanner\n{Tools}dotnet sonarscanner begin /k:\"{build.SonarProjectKey}\" /d:sonar.host.url=\"$SONAR_HOST_URL\" /d:sonar.token=\"$SONAR_TOKEN\""));
                }

                steps.Add(new("Restore", $"dotnet restore {target}" + (build.AuditDependencies ? NuGetAudit : "")));
                steps.Add(new("Build", $"dotnet build {target} -c Release --no-restore"));
                if (build.RunTests)
                {
                    steps.Add(new("Test", $"dotnet test {target} -c Release --no-build"
                        + (build.PublishTestResults ? " --logger trx" : "")
                        + (build.Coverage ? " --collect:\"XPlat Code Coverage\"" : "")
                        + (build.PublishTestResults || build.Coverage ? $" --results-directory {TestResults}" : "")));
                }

                if (sonar)
                {
                    steps.Add(new("Finish the SonarQube analysis", Tools + "dotnet sonarscanner end /d:sonar.token=\"$SONAR_TOKEN\""));
                }

                if (spec.Deploy.Target == Target.Package)
                {
                    steps.Add(new("Pack", $"dotnet pack {project} -c Release -o {PackageFolder}"));
                }
                else if (spec.Deploy.Target == Target.ServiceFabric)
                {
                    steps.Add(FabricPackage(spec));
                }
                else if (package)
                {
                    steps.Add(new("Publish", $"dotnet publish {project} -c Release -o {PackageFolder}"));
                }

                break;
            }

            case Stack.Node:
                steps.Add(new("Install packages", cd + "npm ci"));
                if (build.AuditDependencies)
                {
                    steps.Add(new("Check the packages for known vulnerabilities", cd + "npm audit --audit-level=high"));
                }

                steps.Add(new("Build", cd + "npm run build --if-present"));
                if (build.RunTests)
                {
                    steps.Add(new("Test", build.PublishTestResults && build.WritesJUnit ? JUnitStart + cd + NodeJUnit : cd + "npm test"));
                }

                if (spec.Deploy.Target == Target.Package)
                {
                    steps.Add(new("Pack", $"mkdir -p {PackageFolder}\nroot=\"$PWD\"\n{cd}npm pack --pack-destination \"$root/{PackageFolder}\""));
                }
                else if (package)
                {
                    steps.Add(new("Package", $"mkdir -p {PackageFolder}\ncp -r {Join(build.WorkingDirectory, build.OutputFolder)}/. {PackageFolder}/"));
                }

                break;
            case Stack.Python:
                steps.Add(new("Install packages", cd + "python -m pip install --upgrade pip\npip install -r requirements.txt"));
                if (build.AuditDependencies)
                {
                    steps.Add(new("Check the packages for known vulnerabilities", cd + "pip install pip-audit\npip-audit -r requirements.txt"));
                }

                if (build.RunTests)
                {
                    steps.Add(new("Test", build.PublishTestResults && build.WritesJUnit ? JUnitStart + cd + $"pip install pytest\npython -m pytest --junitxml=\"{JUnitFile}\"" : cd + "pip install pytest\npython -m pytest"));
                }

                if (package)
                {
                    // App Service installs requirements.txt itself when the files arrive (SCM_DO_BUILD_DURING_DEPLOYMENT).
                    steps.Add(new("Package", $"mkdir -p {PackageFolder}\ntar -C {build.WorkingDirectory} --exclude=./out --exclude=./.git --exclude=./.venv --exclude=./.cache -cf - . | tar -xf - -C {PackageFolder}"));
                }

                break;
            case Stack.Java:
                steps.Add(new(build.RunTests ? "Build and test" : "Build", cd + (build.RunTests ? "mvn -B package" : "mvn -B package -DskipTests")));
                if (!string.IsNullOrWhiteSpace(build.SonarProjectKey))
                {
                    // The Sonar plugin reads SONAR_HOST_URL and SONAR_TOKEN from the environment.
                    steps.Add(new("Analyse with SonarQube", cd + $"mvn -B sonar:sonar -Dsonar.projectKey={build.SonarProjectKey}"));
                }
                if (package)
                {
                    steps.Add(new("Package", $"mkdir -p {PackageFolder}\ncp {Join(build.WorkingDirectory, "target")}/*.jar {PackageFolder}/"));
                }

                break;
            case Stack.Gradle:
            {
                // The wrapper of the repository pins the Gradle version; without it the agent's own Gradle builds.
                const string Gradle = "if [ -f ./gradlew ]; then gradle=\"sh ./gradlew\"; else gradle=gradle; fi\n";
                steps.Add(new(build.RunTests ? "Build and test" : "Build", cd + Gradle + (build.RunTests ? "$gradle --no-daemon build" : "$gradle --no-daemon build -x test")));
                if (!string.IsNullOrWhiteSpace(build.SonarProjectKey))
                {
                    // The org.sonarqube plugin of the build reads SONAR_HOST_URL and SONAR_TOKEN from the environment.
                    steps.Add(new("Analyse with SonarQube", cd + Gradle + $"$gradle --no-daemon sonar -Dsonar.projectKey={build.SonarProjectKey}"));
                }

                if (package)
                {
                    steps.Add(new("Package", $"mkdir -p {PackageFolder}\ncp {Join(build.WorkingDirectory, "build/libs")}/*.jar {PackageFolder}/"));
                }

                break;
            }

            case Stack.Android:
                steps.AddRange(Store.AndroidBuild(spec, cd));
                break;
            case Stack.Cpp:
                // With a Conan remote, the packages of the conanfile are installed first and CMake finds them through Conan's toolchain.
                steps.Add(Feeds.UsesConan(spec)
                    ? new("Build", cd + "conan install . --output-folder=build --build=missing -s build_type=Release\ncmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DCMAKE_TOOLCHAIN_FILE=build/conan_toolchain.cmake\ncmake --build build --parallel")
                    : new("Build", cd + "cmake -S . -B build -DCMAKE_BUILD_TYPE=Release\ncmake --build build --parallel"));
                if (build.RunTests)
                {
                    steps.Add(new("Test", build.PublishTestResults ? $"mkdir -p {TestResults}\nroot=\"$PWD\"\n{cd}ctest --test-dir build --output-on-failure --output-junit \"$root/{TestResults}/junit.xml\"" : cd + "ctest --test-dir build --output-on-failure"));
                }

                if (package)
                {
                    // What the project's install rules ship.
                    steps.Add(new("Package", $"mkdir -p {PackageFolder}\nroot=\"$PWD\"\n{cd}cmake --install build --prefix \"$root/{PackageFolder}\""));
                }

                break;
            case Stack.Swift:
                steps.Add(new("Build", cd + "swift build -c release"));
                if (build.RunTests)
                {
                    steps.Add(new("Test", build.PublishTestResults ? $"mkdir -p {TestResults}\nroot=\"$PWD\"\n{cd}swift test --xunit-output \"$root/{TestResults}/junit.xml\"" : cd + "swift test"));
                }

                if (package)
                {
                    steps.Add(new("Package", $"mkdir -p {PackageFolder}\nfind {Join(build.WorkingDirectory, ".build/release")} -maxdepth 1 -type f -perm -u+x -exec cp {{}} {PackageFolder}/ \\;"));
                }

                break;
            case Stack.Scala:
            {
                // sbt in the home folder when the agent or image does not have it.
                const string Sbt = "command -v sbt > /dev/null || { { curl -fsSL https://github.com/sbt/sbt/releases/download/v1.10.7/sbt-1.10.7.tgz || wget -qO- https://github.com/sbt/sbt/releases/download/v1.10.7/sbt-1.10.7.tgz; } | tar -xz -C \"$HOME\"; export PATH=\"$HOME/sbt/bin:$PATH\"; }\n";
                steps.Add(new(build.RunTests ? "Build and test" : "Build", Sbt + cd + (build.RunTests ? "sbt -batch test package" : "sbt -batch package")));
                if (package)
                {
                    steps.Add(new("Package", $"mkdir -p {PackageFolder}\nfind {Join(build.WorkingDirectory, "target")} -path '*/scala-*/*.jar' -exec cp {{}} {PackageFolder}/ \\;"));
                }

                break;
            }

            case Stack.Elixir:
                steps.Add(new("Get the packages", cd + "mix local.hex --force\nmix local.rebar --force\nmix deps.get"));
                steps.Add(new("Build", cd + "mix compile --warnings-as-errors"));
                if (build.RunTests)
                {
                    steps.Add(new("Test", cd + "mix test"));
                }

                if (package)
                {
                    steps.Add(new("Package", $"root=\"$PWD\"\n{cd}MIX_ENV=prod mix release --overwrite --path \"$root/{PackageFolder}\""));
                }

                break;
            case Stack.Flutter:
                steps.Add(new("Get the packages", cd + "flutter pub get"));
                steps.Add(new("Build", cd + "flutter analyze\nflutter build web --release"));
                if (build.RunTests)
                {
                    steps.Add(new("Test", build.PublishTestResults && build.WritesJUnit
                        ? JUnitStart + cd + $"dart pub global activate junitreport > /dev/null\nset -o pipefail\nflutter test --machine | dart pub global run junitreport:tojunit --output \"{JUnitFile}\""
                        : cd + "flutter test"));
                }

                if (package)
                {
                    steps.Add(new("Package", $"mkdir -p {PackageFolder}\ncp -r {Join(build.WorkingDirectory, "build/web")}/. {PackageFolder}/"));
                }

                break;
            case Stack.Ios:
                steps.AddRange(Store.IosBuild(spec));
                break;
            case Stack.Go:
                steps.Add(new("Download the modules", cd + "go mod download"));
                if (build.AuditDependencies)
                {
                    steps.Add(new("Check the modules for known vulnerabilities", cd + "go install golang.org/x/vuln/cmd/govulncheck@latest\n\"$(go env GOPATH)/bin/govulncheck\" ./..."));
                }

                steps.Add(new("Build", cd + "go vet ./...\ngo build ./..."));
                if (build.RunTests)
                {
                    steps.Add(new("Test", build.PublishTestResults
                        ? $"mkdir -p {TestResults}\nroot=\"$PWD\"\n{cd}go install github.com/jstemmer/go-junit-report/v2@latest\ngo test -v ./... 2>&1 | \"$(go env GOPATH)/bin/go-junit-report\" -set-exit-code > \"$root/{TestResults}/junit.xml\""
                        : cd + "go test ./..."));
                }

                if (package)
                {
                    // Every program (package main) of the module becomes one file in out/app, without C libraries to install.
                    steps.Add(new("Package", $"mkdir -p {PackageFolder}\nroot=\"$PWD\"\n{cd}CGO_ENABLED=0 go build -o \"$root/{PackageFolder}/\" ./..."));
                }

                break;
            case Stack.Ruby:
                steps.Add(new("Install the gems", cd + "bundle install --jobs 4"));
                if (build.AuditDependencies)
                {
                    steps.Add(new("Check the gems for known vulnerabilities", cd + "gem install bundler-audit\nbundle-audit check --update"));
                }

                if (build.RunTests)
                {
                    // RSpec writes JUnit XML with a formatter gem: a second Gemfile adds it to the project's own, which stays as it is.
                    steps.Add(new("Test", build.PublishTestResults && build.WritesJUnit
                        ? JUnitStart + cd + "if [ -d spec ]; then\n  printf 'eval_gemfile \"Gemfile\"\\ngem \"rspec_junit_formatter\"\\n' > Gemfile.results\n  [ -f Gemfile.lock ] && cp Gemfile.lock Gemfile.results.lock\n  export BUNDLE_GEMFILE=\"$PWD/Gemfile.results\" BUNDLE_FROZEN=false\n  bundle install --quiet\n"
                            + $"  bundle exec rspec --format progress --format RspecJunitFormatter --out \"{JUnitFile}\"\nelse\n  bundle exec rake\n  echo \"The tests are not RSpec: the results are in the log.\"\n  echo '<testsuites/>' > \"{JUnitFile}\"\nfi"
                        : cd + "bundle exec rake"));
                }

                if (package)
                {
                    steps.Add(new("Package", SourcePackage(build, "./vendor/bundle")));
                }

                break;
            case Stack.Php:
                steps.Add(new("Install the packages", cd + "composer install --no-interaction --prefer-dist"));
                if (build.AuditDependencies)
                {
                    steps.Add(new("Check the packages for known vulnerabilities", cd + "composer audit"));
                }

                if (build.RunTests)
                {
                    steps.Add(new("Test", build.PublishTestResults ? $"mkdir -p {TestResults}\nroot=\"$PWD\"\n{cd}vendor/bin/phpunit --log-junit \"$root/{TestResults}/junit.xml\"" : cd + "vendor/bin/phpunit"));
                }

                if (package)
                {
                    // What is deployed carries the packages the application runs with, without the development ones.
                    steps.Add(new("Package", cd + "composer install --no-interaction --prefer-dist --no-dev --optimize-autoloader\n" + (cd.Length > 0 ? "cd \"$OLDPWD\"\n" : "") + SourcePackage(build, "./tests")));
                }

                break;
            case Stack.Rust:
                if (build.AuditDependencies)
                {
                    steps.Add(new("Check the crates for known vulnerabilities", cd + "cargo install cargo-audit --locked\ncargo audit"));
                }

                steps.Add(new("Build", cd + "cargo build --release --locked"));
                if (build.RunTests)
                {
                    // cargo-nextest writes JUnit XML; the results are kept even when a test fails.
                    steps.Add(new("Test", build.PublishTestResults && build.WritesJUnit
                        ? JUnitStart + cd + "curl -LsSf https://get.nexte.st/latest/linux | tar zxf - -C \"${CARGO_HOME:-$HOME/.cargo}/bin\"\nprintf '[profile.ci.junit]\\npath = \"junit.xml\"\\n' > \"$root/out/nextest.toml\"\ncode=0\ncargo nextest run --release --locked --config-file \"$root/out/nextest.toml\" --profile ci || code=$?\n" + $"cp target/nextest/ci/junit.xml \"{JUnitFile}\" 2> /dev/null || true\nexit $code"
                        : cd + "cargo test --release --locked"));
                }

                if (package)
                {
                    // The programs of the release build: the executable files directly under target/release.
                    steps.Add(new("Package", $"mkdir -p {PackageFolder}\nfind {Join(build.WorkingDirectory, "target/release")} -maxdepth 1 -type f -perm -u+x -exec cp {{}} {PackageFolder}/ \\;"));
                }

                break;
        }

        return steps;
    }

    /// <summary>The source folder as it is, without what never ships (and one more folder the language adds).</summary>
    private static string SourcePackage(BuildSettings build, string alsoExclude) =>
        $"mkdir -p {PackageFolder}\ntar -C {build.WorkingDirectory} --exclude=./out --exclude=./.git --exclude={alsoExclude} -cf - . | tar -xf - -C {PackageFolder}";

    // Finds MSBuild through vswhere, which every Visual Studio and Build Tools installation has.
    private const string FindMsBuild = """
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if (-not $msbuild) { throw 'MSBuild was not found on this agent.' }

        """;

    /// <summary>Service Fabric: MSBuild restores the application project and writes its package (services, code and manifests) into out/app.</summary>
    private static ScriptStep FabricPackage(PipelineSpec spec)
    {
        var project = spec.Deploy.FabricProject;
        return new("Package the Service Fabric application", FindMsBuild.Replace("\r\n", "\n")
            + $"& $msbuild {project} -t:Restore -p:RestorePackagesConfig=true -p:Configuration=Release -nologo\nif ($LASTEXITCODE -ne 0) {{ throw 'The restore failed.' }}\n"
            + "$out = (Join-Path (Get-Location) 'out\\app') -replace '\\\\', '/'\n"
            + $"& $msbuild {project} -t:Package -p:Configuration=Release -p:Platform=x64 \"-p:PackageLocation=$out\" -nologo\nif ($LASTEXITCODE -ne 0) {{ throw 'The package failed.' }}");
    }

    /// <summary>.NET Framework on a Windows agent, in PowerShell: MSBuild restores (packages.config too), builds and publishes; vstest runs the tests.</summary>
    private static List<ScriptStep> Framework(PipelineSpec spec)
    {
        var build = spec.Build;
        var project = build.ProjectPath ?? "";
        var target = string.IsNullOrWhiteSpace(build.TestPath) ? project : build.TestPath;
        var find = FindMsBuild.Replace("\r\n", "\n");
        static string Check(string what) => $"\nif ($LASTEXITCODE -ne 0) {{ throw 'The {what} failed.' }}";
        var steps = new List<ScriptStep>
        {
            new("Restore", $"{find}& $msbuild {target} -t:Restore -p:RestorePackagesConfig=true -p:Configuration=Release -m -nologo{Check("restore")}"),
            new("Build", $"{find}& $msbuild {target} -p:Configuration=Release -m -nologo{Check("build")}"),
        };
        if (build.RunTests)
        {
            steps.Add(new("Test", string.Join('\n',
            [
                "$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\\Installer\\vswhere.exe'",
                "$vstest = & $vswhere -latest -products * -find 'Common7\\IDE\\CommonExtensions\\Microsoft\\TestWindow\\vstest.console.exe' | Select-Object -First 1",
                "if (-not $vstest) { throw 'vstest.console.exe was not found on this agent.' }",
                "$tests = Get-ChildItem -Recurse -Filter '*Tests.dll' | Where-Object { $_.FullName -match '\\\\bin\\\\Release\\\\' } | ForEach-Object { $_.FullName }",
                "if (-not $tests) { throw 'No test assemblies (*Tests.dll) were found under bin\\Release.' }",
                "& $vstest $tests" + Check("tests"),
            ])));
        }

        if (spec.Deploy.Target == Target.ServiceFabric)
        {
            steps.Add(FabricPackage(spec));
        }
        else if (spec.UsesPackage)
        {
            // A web application is published the way Visual Studio does it; anything else is its build output.
            var web = spec.Deploy.Target is Target.Iis or Target.AppService;
            var output = "$out = (Join-Path (Get-Location) 'out\\app') -replace '\\\\', '/'\n";
            steps.Add(new("Publish", find + output + (web
                ? $"& $msbuild {project} -p:Configuration=Release -p:DeployOnBuild=true -p:WebPublishMethod=FileSystem -p:DeleteExistingFiles=true \"-p:publishUrl=$out\" -nologo"
                : $"& $msbuild {project} -p:Configuration=Release \"-p:OutDir=$out/\" -nologo") + Check("publish")));
        }

        return steps;
    }

    /// <summary>What App Service and Function App receive: the folder, or the jar for Java.</summary>
    public static string PackagePattern(PipelineSpec spec, string folder) =>
        spec.Build.IsJvm && spec.Build.CustomCommands.Count == 0 ? folder + "/*.jar" : folder;

    public static string Image(PipelineSpec spec, string tag) => $"{spec.Deploy.Registry}.azurecr.io/{spec.Deploy.ImageName}:{tag}";

    public static string BuildImage(PipelineSpec spec, string tag) =>
        $"az acr build --registry {spec.Deploy.Registry} --image {spec.Deploy.ImageName}:{tag} --file {spec.Deploy.Dockerfile}{Feeds.DockerArguments(spec)} {spec.Deploy.DockerContext}";

    public static string ContainerAppUpdate(PipelineSpec spec, EnvironmentSpec environment, string image)
    {
        var update = $"az containerapp update --name {environment.ResourceName} --resource-group {environment.ResourceGroup} --image {image}{DeployExtras.ContainerSettings(environment)}";
        return spec.Deploy.Strategy == DeployStrategy.Standard ? Strategies.ContainerAppStandard(environment, update) : Strategies.ContainerApp(spec, environment, update);
    }

    /// <summary>Installs kubectl (and kubelogin) in the home folder, so it works without sudo on every runner.</summary>
    public static string AksDeploy(PipelineSpec spec, EnvironmentSpec environment, string image)
    {
        var deploy = spec.Deploy;
        var lines = new List<string>
        {
            "mkdir -p \"$HOME/.local/bin\"",
            "export PATH=\"$HOME/.local/bin:$PATH\"",
            "az aks install-cli --install-location \"$HOME/.local/bin/kubectl\" --kubelogin-install-location \"$HOME/.local/bin/kubelogin\"",
            $"az aks get-credentials --resource-group {environment.ResourceGroup} --name {environment.ResourceName} --overwrite-existing",
        };
        if (deploy.EntraIdCluster)
        {
            lines.Add("kubelogin convert-kubeconfig -l azurecli");
        }

        lines.Add(Kube.Rollout(spec, environment, image));
        return string.Join('\n', lines);
    }

    public static string TerraformCheck() =>
        "terraform fmt -check -recursive\nterraform init -input=false -backend=false\nterraform validate";

    /// <summary>Each environment keeps its own state file; the code needs an empty backend "azurerm" {} block.</summary>
    public static string TerraformDeploy(PipelineSpec spec, EnvironmentSpec environment, bool oidc = true)
    {
        var deploy = spec.Deploy;
        var varFile = string.IsNullOrWhiteSpace(environment.ParametersFile) ? "" : $" -var-file={environment.ParametersFile}";
        if (!spec.NeedsAzure)
        {
            // The state of each environment in the cloud's own bucket; the code has an empty backend "s3" {} or "gcs" {} block.
            var bucket = spec.IsAws
                ? $"terraform init -input=false -backend-config=bucket={deploy.Bucket} -backend-config=key={spec.Name}-{environment.Name}.tfstate -backend-config=region={deploy.CloudRegion}"
                : $"terraform init -input=false -backend-config=bucket={deploy.Bucket} -backend-config=prefix={spec.Name}-{environment.Name}";
            return deploy.Preview
                ? $"{bucket}\nterraform plan -input=false{varFile} -out=tfplan\nterraform apply -input=false tfplan"
                : $"{bucket}\nterraform apply -input=false -auto-approve{varFile}";
        }

        var init = string.Join(" \\\n  ", new[]
        {
            "terraform init -input=false",
            $"-backend-config=resource_group_name={deploy.StateResourceGroup}",
            $"-backend-config=storage_account_name={deploy.StateStorageAccount}",
            $"-backend-config=container_name={deploy.StateContainer}",
            $"-backend-config=key={spec.Name}-{environment.Name}.tfstate",
            oidc ? "-backend-config=use_oidc=true" : null,
            "-backend-config=use_azuread_auth=true",
        }.Where(line => line is not null));
        return deploy.Preview
            ? $"{init}\nterraform plan -input=false{varFile} -out=tfplan\nterraform apply -input=false tfplan"
            : $"{init}\nterraform apply -input=false -auto-approve{varFile}";
    }

    /// <summary>Bicep compiles the template; an ARM template is JSON, so it is read as JSON.</summary>
    public static string BicepCheck(PipelineSpec spec) => spec.Deploy.Target == Target.ArmTemplate
        ? $"python3 -m json.tool {spec.Deploy.BicepTemplate} > /dev/null"
        : $"az bicep build --file {spec.Deploy.BicepTemplate} --stdout > /dev/null";

    public static string BicepDeploy(PipelineSpec spec, EnvironmentSpec environment, string runId)
    {
        var parameters = string.IsNullOrWhiteSpace(environment.ParametersFile) ? "" : $" --parameters {environment.ParametersFile}";
        var arguments = $"--resource-group {environment.ResourceGroup} --template-file {spec.Deploy.BicepTemplate}{parameters}";
        var create = $"az deployment group create --name {spec.Name}-{environment.Name}-{runId} {arguments}";
        return spec.Deploy.Preview ? $"az deployment group what-if {arguments}\n{create}" : create;
    }

    /// <summary>Installs the chosen Terraform release in the home folder, for runners that do not have it.</summary>
    public static string InstallTerraform(PipelineSpec spec)
    {
        var version = spec.Deploy.TerraformVersion;
        return string.Join('\n',
        [
            "mkdir -p \"$HOME/.local/bin\"",
            "export PATH=\"$HOME/.local/bin:$PATH\"",
            $"curl -sSLo /tmp/terraform.zip https://releases.hashicorp.com/terraform/{version}/terraform_{version}_linux_amd64.zip",
            "unzip -o -q /tmp/terraform.zip -d \"$HOME/.local/bin\"",
        ]);
    }

    /// <summary>The person's commands as one script, one command per line.</summary>
    public static string Lines(IEnumerable<string> commands) => string.Join('\n', commands.Select(command => command.Trim()));

    /// <summary>Their own commands run in this environment, so they get DEPLOY_ENVIRONMENT with its name.</summary>
    public static bool HasOwnCommands(PipelineSpec spec, EnvironmentSpec environment) =>
        spec.Deploy.Target == Target.Script || environment.BeforeDeploy.Count > 0 || After(environment).Count > 0;

    /// <summary>The name the feed a package is published to has in the commands and in its secrets (FEED_PUBLISH_TOKEN).</summary>
    public const string PublishName = "publish";

    /// <summary>Publishes the packed .nupkg or .tgz files; folder is where the deploy job has the build.</summary>
    public static ExtraStep PublishPackage(PipelineSpec spec, Platform platform, string folder)
    {
        var feed = spec.Deploy.PublishFeed!;
        feed.Name = PublishName;
        var credential = Feeds.Credential(feed, platform);
        var url = Feeds.Url(feed);
        if (feed.Type == FeedType.NuGet)
        {
            // The key is only a placeholder: the feed reads the user and token of the source.
            return new(
                "Publish the package",
                $"dotnet nuget add source \"{url}\" --name {PublishName}\ndotnet nuget push \"{folder}/*.nupkg\" --source {PublishName} --api-key az --skip-duplicate",
                [new(Feeds.NuGetCredentialVariable(feed), Feeds.NuGetCredentialValue(credential))]);
        }

        var key = Feeds.NpmAuthKey(feed);
        var sign = feed.Kind == FeedKind.AzureArtifacts
            ? $"npm config set \"{key}:username\" {Feeds.AzureArtifactsUser}\nnpm config set \"{key}:_password\" \"$(printf '%s' \"$PUBLISH_TOKEN\" | base64 | tr -d '\\n')\"\nnpm config set \"{key}:email\" npm-requires-an-email@example.com"
            : $"npm config set \"{key}:_authToken\" \"$PUBLISH_TOKEN\"";
        return new(
            "Publish the package",
            $"{sign}\nfor package in {folder}/*.tgz; do\n  npm publish \"$package\" --registry \"{url}\"\ndone",
            [new("PUBLISH_TOKEN", credential.Token)]);
    }

    /// <summary>Posts TEXT to the incoming webhook in WEBHOOK (the JSON Slack and compatible services accept).</summary>
    /// <summary>The folder of the results, made before the step moves into the project's folder.</summary>
    private const string JUnitStart = $"mkdir -p {TestResults}\nroot=\"$PWD\"\n";

    private const string JUnitFile = $"$root/{TestResults}/junit.xml";

    /// <summary>npm test with the JUnit reporter of the test tool the project uses: Vitest, Jest or Mocha.</summary>
    private const string NodeJUnit =
        "if grep -q '\"vitest\"' package.json; then\n  npm test -- --reporter=default --reporter=junit --outputFile.junit=\"" + JUnitFile + "\"\n"
        + "elif grep -q '\"jest\"' package.json; then\n  npm install --no-save jest-junit\n  JEST_JUNIT_OUTPUT_FILE=\"" + JUnitFile + "\" npm test -- --reporters=default --reporters=jest-junit\n"
        + "elif grep -q '\"mocha\"' package.json; then\n  npm install --no-save mocha-junit-reporter\n  MOCHA_FILE=\"" + JUnitFile + "\" npm test -- --reporter mocha-junit-reporter\n"
        + "else\n  npm test\n  echo \"No JUnit reporter is known for this project's test tool: the results are in the log.\"\n  echo '<testsuites/>' > \"" + JUnitFile + "\"\nfi";

    public const string Notify = "curl -fsS -X POST -H 'Content-Type: application/json' --data \"{\\\"text\\\": \\\"$TEXT\\\"}\" \"$WEBHOOK\"";

    /// <summary>Where the JUnit XML files of a Java build are, as a pattern from the repository root.</summary>
    public static string JUnitFiles(PipelineSpec spec) => spec.Build.Stack switch
    {
        Stack.Gradle => "**/build/test-results/test/TEST-*.xml",
        Stack.Android => "**/build/test-results/**/TEST-*.xml",
        Stack.Scala => "**/target/test-reports/*.xml",
        Stack.Java => "**/surefire-reports/TEST-*.xml",
        _ => TestResults + "/*.xml",
    };

    /// <summary>The folder with the test results: where the JUnit files are, or out/test-results for the builds that write there.</summary>
    public static string JUnitFolder(PipelineSpec spec) => spec.Build.Stack switch
    {
        Stack.Gradle => "**/build/test-results/test",
        Stack.Android => "**/build/test-results",
        Stack.Scala => "**/target/test-reports",
        Stack.Java => "**/target/surefire-reports",
        _ => TestResults,
    };

    /// <summary>The commands after a deploy: the person's own, then the end-to-end and load tests asked for.</summary>
    public static IReadOnlyList<string> After(EnvironmentSpec environment)
    {
        var lines = new List<string>(environment.AfterDeploy);
        if (!string.IsNullOrWhiteSpace(environment.EndToEndFolder))
        {
            lines.Add($"(cd {environment.EndToEndFolder} && npm ci && npx playwright install --with-deps chromium && npx playwright test)");
        }

        if (!string.IsNullOrWhiteSpace(environment.LoadTestScript))
        {
            lines.Add($"curl -fsSL https://github.com/grafana/k6/releases/download/v0.55.0/k6-v0.55.0-linux-amd64.tar.gz | tar -xz -C /tmp && /tmp/k6-v0.55.0-linux-amd64/k6 run {environment.LoadTestScript}");
        }

        return lines;
    }

    /// <summary>The tests after the deploy are files of the repository, so the deploy job fetches it.</summary>
    public static bool TestsAfter(EnvironmentSpec environment) => !string.IsNullOrWhiteSpace(environment.EndToEndFolder) || !string.IsNullOrWhiteSpace(environment.LoadTestScript);

    /// <summary>What the deploy step of a commands target runs: the person's own commands, or the az commands of an Azure service.</summary>
    public static IReadOnlyList<string> DeployLines(PipelineSpec spec, EnvironmentSpec environment)
    {
        var deploy = spec.Deploy;
        var resource = $"--resource-group {environment.ResourceGroup}";
        return deploy.Target switch
        {
            Target.ApiManagement => [$"az apim api import {resource} --service-name {environment.ResourceName} --api-id {deploy.AppId} --path {deploy.AppId} --specification-format OpenApi --specification-path {deploy.BicepTemplate} --output none"],
            Target.LogicApp => [$"python3 -m zipfile -c logicapp.zip {deploy.InfraDirectory}/.", $"az logicapp deployment source config-zip {resource} --name {environment.ResourceName} --src logicapp.zip"],
            Target.DataFactory => [$"az deployment group create {resource} --name {spec.Name}-{environment.Name} --template-file {deploy.BicepTemplate} --parameters factoryName={environment.ResourceName}{(string.IsNullOrWhiteSpace(environment.ParametersFile) ? "" : $" --parameters {environment.ParametersFile}")} --output none"],
            _ => deploy.DeployCommands,
        };
    }

    /// <summary>GitLab, Bitbucket and CircleCI start from a plain image: what the build needs installed first.</summary>
    public static string? ImagePreparation(PipelineSpec spec) => spec.Build.CustomCommands.Count > 0 ? null : spec.Build.Stack switch
    {
        // Composer downloads zip files and reads git repositories.
        Stack.Php => "apt-get update -qq && apt-get install -y -qq git unzip > /dev/null\ncurl -sS https://getcomposer.org/installer | php -- --install-dir=/usr/local/bin --filename=composer --quiet",
        Stack.Cpp => "apt-get update -qq && apt-get install -y -qq cmake > /dev/null",
        // The Android image has the SDK and Java; React Native also needs Node.js.
        Stack.Android when spec.Build.MobileFramework == MobileFramework.ReactNative => "apt-get update -qq && apt-get install -y -qq nodejs npm > /dev/null",
        _ => null,
    };

    /// <summary>The Docker image with the language's tools, for the systems whose jobs start from an image.</summary>
    public static string BuildImage(PipelineSpec spec)
    {
        var version = spec.Build.EffectiveVersion;
        return spec.Build.Stack switch
        {
            Stack.DotNet => $"mcr.microsoft.com/dotnet/sdk:{version}",
            Stack.Node => $"node:{version}",
            Stack.Python => $"python:{version}",
            Stack.Java => $"maven:3-eclipse-temurin-{version}",
            Stack.Gradle => $"gradle:jdk{version}",
            Stack.Android => "ghcr.io/cirruslabs/android-sdk:35",
            Stack.Cpp => "gcc:14",
            Stack.Swift => $"swift:{version}",
            Stack.Scala => $"eclipse-temurin:{version}",
            Stack.Elixir => $"elixir:{version}",
            Stack.Flutter => $"ghcr.io/cirruslabs/flutter:{version}",
            Stack.Go => $"golang:{version}",
            Stack.Ruby => $"ruby:{version}",
            Stack.Php => $"php:{version}-cli",
            Stack.Rust => version == "stable" ? "rust:latest" : $"rust:{version}",
            _ => "ubuntu:24.04",
        };
    }

    public static string Join(string folder, string child) => folder is "." or "./" ? child : folder.TrimEnd('/') + "/" + child;
}
