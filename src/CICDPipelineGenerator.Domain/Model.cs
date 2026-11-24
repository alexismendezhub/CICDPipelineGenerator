using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace CICDPipelineGenerator.Domain;

/// <summary>The pipeline systems CI/CD Pipeline Generator writes YAML for.</summary>
public enum Platform
{
    GitHubActions,
    AzureDevOps,
    GitLab,
    /// <summary>A declarative Jenkinsfile for a multibranch pipeline.</summary>
    Jenkins,
    /// <summary>bitbucket-pipelines.yml.</summary>
    Bitbucket,
    /// <summary>.circleci/config.yml.</summary>
    CircleCi,
    /// <summary>.travis.yml.</summary>
    TravisCi,
    /// <summary>cloudbuild.yaml (Google Cloud Build).</summary>
    CloudBuild,
    /// <summary>buildspec files for the CodeBuild projects of an AWS CodePipeline.</summary>
    CodePipeline,
    /// <summary>.teamcity/settings.kts (Kotlin DSL).</summary>
    TeamCity,
    /// <summary>A WorkflowTemplate for Argo Workflows.</summary>
    ArgoWorkflows,
}

/// <summary>What the code is written in; None means there is nothing to compile (a Dockerfile or infrastructure only).</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Stack is the technology stack (the language), not a stack collection.")]
public enum Stack
{
    DotNet,
    Node,
    Python,
    Java,
    None,
    /// <summary>.NET Framework (4.x): MSBuild and vstest on a Windows agent.</summary>
    DotNetFramework,
    /// <summary>Java or Kotlin built with Gradle (the wrapper of the repository when it has one).</summary>
    Gradle,
    Go,
    /// <summary>Ruby with Bundler.</summary>
    Ruby,
    /// <summary>PHP with Composer.</summary>
    Php,
    /// <summary>Rust with Cargo.</summary>
    Rust,
    /// <summary>An Android app built with its Gradle wrapper into a signed bundle (.aab).</summary>
    Android,
    /// <summary>An iOS app archived and exported with xcodebuild on a macOS agent.</summary>
    Ios,
    /// <summary>A Windows app packaged as MSIX with MSBuild on a Windows agent.</summary>
    Msix,
    /// <summary>C or C++ with CMake.</summary>
    Cpp,
    /// <summary>Swift on the server, with the Swift package manager.</summary>
    Swift,
    /// <summary>Scala with sbt.</summary>
    Scala,
    /// <summary>Elixir with Mix; what is deployed is a release.</summary>
    Elixir,
    /// <summary>A Flutter web application.</summary>
    Flutter,
}

/// <summary>What an Android or iOS app is written with.</summary>
public enum MobileFramework
{
    /// <summary>The platform's own project (Gradle, Xcode).</summary>
    Native,
    /// <summary>React Native: the JavaScript packages first, then the native project in android/ or ios/.</summary>
    ReactNative,
    /// <summary>.NET MAUI: dotnet publish for the platform.</summary>
    Maui,
}

/// <summary>Where the build job runs.</summary>
public enum BuildAgent
{
    HostedLinux,
    HostedWindows,
    /// <summary>Your own Linux runner or agent pool, named by its label.</summary>
    SelfHostedLinux,
    /// <summary>Your own Windows runner or agent pool, named by its label.</summary>
    SelfHostedWindows,
    /// <summary>A hosted macOS agent (iOS builds).</summary>
    HostedMac,
    /// <summary>Your own macOS runner or agent, named by its label.</summary>
    SelfHostedMac,
}

/// <summary>How the database is brought up to date before each deploy.</summary>
public enum MigrationKind
{
    None,
    /// <summary>An Entity Framework Core migrations bundle, built with the code and run against each environment.</summary>
    EfCoreBundle,
    /// <summary>The .sql files of a folder, run in name order with sqlcmd; they must be safe to run again.</summary>
    SqlScripts,
    /// <summary>A .dacpac of the build, published with sqlpackage.</summary>
    Dacpac,
}

/// <summary>Where the pipeline deploys.</summary>
public enum Target
{
    AppService,
    FunctionApp,
    StaticWebApp,
    ContainerApp,
    Aks,
    Terraform,
    Bicep,
    /// <summary>A Windows service on your own servers: stop, back up, copy, start, check, and roll back on failure.</summary>
    WindowsService,
    /// <summary>An IIS site on your own servers: stop the application pool, back up, copy, start, check, roll back.</summary>
    Iis,
    /// <summary>A systemd service on your own Linux servers: stop, back up, copy with rsync, start, check, roll back.</summary>
    LinuxService,
    /// <summary>A scheduled task on your own servers: disable and stop it, back up, copy, enable it again.</summary>
    ScheduledTask,
    /// <summary>A NuGet or npm package published to a feed.</summary>
    Package,
    /// <summary>AWS: a static site synchronised to an S3 bucket.</summary>
    AwsS3,
    /// <summary>AWS: the code of a Lambda function.</summary>
    AwsLambda,
    /// <summary>AWS: a new version of an Elastic Beanstalk environment.</summary>
    AwsElasticBeanstalk,
    /// <summary>AWS: a Docker image pushed to ECR and rolled out to an ECS service.</summary>
    AwsEcs,
    /// <summary>Google Cloud: a Docker image built with Cloud Build and deployed to Cloud Run.</summary>
    GcpCloudRun,
    /// <summary>Google Cloud: a function deployed from its source.</summary>
    GcpCloudFunctions,
    /// <summary>Google Cloud: an App Engine application deployed from its app.yaml.</summary>
    GcpAppEngine,
    /// <summary>Google Cloud: a Docker image built with Cloud Build and rolled out to a GKE deployment.</summary>
    GcpGke,
    /// <summary>Your own deploy commands, on a hosted runner (bash) or on the environment's server (PowerShell).</summary>
    Script,
    /// <summary>AWS: a Docker image pushed to ECR and rolled out to a deployment of an EKS cluster.</summary>
    AwsEks,
    /// <summary>Any Kubernetes cluster, reached with the kubeconfig kept in the secret KUBE_CONFIG; the image goes to Docker Hub, GitHub Container Registry or another registry.</summary>
    Kubernetes,
    /// <summary>A Docker image built and pushed to Docker Hub, GitHub Container Registry or another registry; each environment gets its tag.</summary>
    ContainerRegistry,
    /// <summary>An Azure Resource Manager template (JSON) deployed into each environment's resource group.</summary>
    ArmTemplate,
    /// <summary>AWS: a CloudFormation template deployed as one stack per environment.</summary>
    CloudFormation,
    /// <summary>AWS: a CDK application; each environment deploys its stack.</summary>
    AwsCdk,
    /// <summary>A Pulumi project; each environment is one of its stacks.</summary>
    Pulumi,
    /// <summary>Google Play: the bundle is uploaded to the first environment's track and promoted to the next ones.</summary>
    GooglePlay,
    /// <summary>TestFlight: the .ipa is uploaded, and each environment is a group of testers that gets it.</summary>
    AppStore,
    /// <summary>The Microsoft Store: each environment is a package flight, or the public submission.</summary>
    MicrosoftStore,
    /// <summary>Netlify: the built site is uploaded to each environment's site.</summary>
    Netlify,
    /// <summary>Vercel: the built site is deployed to each environment's project.</summary>
    Vercel,
    /// <summary>Cloudflare Pages: the built site is uploaded to each environment's project.</summary>
    CloudflarePages,
    /// <summary>Firebase Hosting: the site described by firebase.json is deployed to each environment's project.</summary>
    FirebaseHosting,
    /// <summary>AWS SAM: the serverless application of the repository is built and deployed as one stack per environment.</summary>
    AwsSam,
    /// <summary>AWS App Runner: a Docker image pushed to ECR that the service is moved to.</summary>
    AwsAppRunner,
    /// <summary>DigitalOcean App Platform: the app builds from the repository itself; the pipeline starts its deployment and waits for it.</summary>
    DigitalOcean,
    /// <summary>Only the database: the migrations run against each environment and nothing else is deployed.</summary>
    Database,
    /// <summary>Azure API Management: the API is imported from its OpenAPI file.</summary>
    ApiManagement,
    /// <summary>Azure Logic Apps (Standard): the workflows folder is uploaded as a zip.</summary>
    LogicApp,
    /// <summary>Azure Data Factory: the ARM template the factory publishes is deployed into each environment's factory.</summary>
    DataFactory,
    /// <summary>Heroku: the Dockerfile is built, pushed to Heroku's registry and released as the web process.</summary>
    Heroku,
    /// <summary>Service Fabric: the application package is uploaded to the cluster and the application created or upgraded.</summary>
    ServiceFabric,
    /// <summary>GitOps (Argo CD, Flux): the image is pushed and its tag written into the manifests repository, which the cluster follows.</summary>
    GitOps,
}

/// <summary>How a new version takes over from the one that is running.</summary>
public enum DeployStrategy
{
    /// <summary>The target's own update (a rolling update where it has one).</summary>
    Standard,
    /// <summary>The new version starts next to the old one without traffic; when it is healthy, all the traffic moves to it at once.</summary>
    BlueGreen,
    /// <summary>The new version gets a share of the traffic first; after a wait (and its health check) it gets all of it.</summary>
    Canary,
}

/// <summary>The cloud a Pulumi project manages, which decides how the pipeline signs in.</summary>
public enum CloudProvider
{
    Azure,
    Aws,
    Gcp,
}

/// <summary>Where the image of a Kubernetes cluster of your own, or of a registry target, is kept.</summary>
public enum RegistryKind
{
    /// <summary>Docker Hub: the registry setting is the user or organisation.</summary>
    DockerHub,
    /// <summary>GitHub Container Registry (ghcr.io): the registry setting is the owner.</summary>
    GitHub,
    /// <summary>Any other registry: the registry setting is its address, with a path when it has one (registry.contoso.com/team).</summary>
    Other,
}

/// <summary>How code moves between environments.</summary>
public enum Flow
{
    /// <summary>One branch; every environment deploys in order, each after the one before it.</summary>
    Promotion,

    /// <summary>Each environment deploys from its own branch (develop to dev, main to prod).</summary>
    BranchPerEnvironment,
    /// <summary>A tag (v1.2.3) deploys every environment in order; pushes to the branch only build.</summary>
    Tag,
}

public enum HostOs
{
    Linux,
    Windows,
}

/// <summary>Everything the person chose on the screen; the generators read only this.</summary>
public sealed class PipelineSpec
{
    public string Name { get; set; } = "my-app";

    /// <summary>owner/repository (GitHub) or group/project (GitLab), used only for the setup guide.</summary>
    public string? Repository { get; set; }

    /// <summary>The repository that keeps the shared templates (owner/name, group/project or Project/Repository); empty writes the whole pipeline in one file.</summary>
    public string? TemplateRepository { get; set; }

    /// <summary>GitHub Actions, Azure DevOps and GitLab with Azure: also write the scripts that do the one-time setup (identities, roles, environments, variables).</summary>
    public bool SetupScript { get; set; } = true;

    /// <summary>The branch or tag of the templates repository the pipeline uses.</summary>
    public string TemplateRef { get; set; } = "main";

    public List<Platform> Platforms { get; set; } = [Platform.GitHubActions];
    public BuildSettings Build { get; set; } = new();
    public DeploySettings Deploy { get; set; } = new();
    public Flow Flow { get; set; } = Flow.Promotion;
    public string MainBranch { get; set; } = "main";
    public bool BuildPullRequests { get; set; } = true;
    public bool ManualRun { get; set; } = true;

    /// <summary>When set, the pipeline also runs on this schedule (cron, five fields, UTC), as a push to the branch that deploys would.</summary>
    public string? Schedule { get; set; }

    /// <summary>When not empty, a push runs the pipeline only if one of these paths changed.</summary>
    public List<string> PathFilters { get; set; } = [];

    public List<EnvironmentSpec> Environments { get; set; } =
    [
        new() { Name = "dev", Branch = "develop" },
        new() { Name = "prod", Branch = "main", RequireApproval = true },
    ];

    [JsonIgnore]
    public bool IsContainer => IsAzureContainer || IsGenericRegistry || Deploy.Target is Target.AwsEcs or Target.AwsEks or Target.AwsAppRunner or Target.GcpCloudRun or Target.GcpGke;

    /// <summary>The image is built with Docker and pushed to a registry that is not a cloud's own (Docker Hub, ghcr.io, another).</summary>
    [JsonIgnore]
    public bool IsGenericRegistry => Deploy.Target is Target.Kubernetes or Target.ContainerRegistry or Target.GitOps;

    [JsonIgnore]
    public bool IsKubernetes => Deploy.Target is Target.Aks or Target.GcpGke or Target.AwsEks or Target.Kubernetes;

    /// <summary>The cluster is updated with a Helm chart instead of manifests and kubectl set image.</summary>
    [JsonIgnore]
    public bool UsesHelm => IsKubernetes && !string.IsNullOrWhiteSpace(Deploy.HelmChart);

    /// <summary>The deploy is the command line of AWS or Google Cloud, or of Docker and kubectl: no Azure sign-in.</summary>
    [JsonIgnore]
    public bool IsCloudCli => IsAws || IsGcp || IsGenericRegistry || IsStore || IsTokenHost || Deploy.Target == Target.Database;

    /// <summary>The deploy publishes to an application store.</summary>
    [JsonIgnore]
    public bool IsStore => Deploy.Target is Target.GooglePlay or Target.AppStore or Target.MicrosoftStore;

    /// <summary>The build runs on macOS (bash, with Xcode).</summary>
    [JsonIgnore]
    public bool IsMacBuild => Build.Agent is BuildAgent.HostedMac or BuildAgent.SelfHostedMac;

    /// <summary>The deploy job needs macOS too: uploading to App Store Connect uses Apple's own tools.</summary>
    [JsonIgnore]
    public bool DeploysOnMac => Deploy.Target == Target.AppStore;

    /// <summary>The image is built in Azure Container Registry.</summary>
    [JsonIgnore]
    public bool IsAzureContainer => Deploy.Target is Target.ContainerApp or Target.Aks;

    [JsonIgnore]
    public bool IsAws => Deploy.Target is Target.AwsS3 or Target.AwsLambda or Target.AwsElasticBeanstalk or Target.AwsEcs or Target.AwsEks or Target.CloudFormation or Target.AwsCdk or Target.AwsSam or Target.AwsAppRunner
        || (Deploy.Target is Target.Pulumi or Target.Terraform && Deploy.InfraCloud == CloudProvider.Aws);

    [JsonIgnore]
    public bool IsGcp => Deploy.Target is Target.GcpCloudRun or Target.GcpCloudFunctions or Target.GcpAppEngine or Target.GcpGke or Target.FirebaseHosting
        || (Deploy.Target is Target.Pulumi or Target.Terraform && Deploy.InfraCloud == CloudProvider.Gcp);

    /// <summary>The tool signs in by itself with the cloud's libraries; the job never runs aws or gcloud.</summary>
    [JsonIgnore]
    public bool SignsInWithLibraries => Deploy.Target is Target.AwsCdk or Target.Pulumi or Target.FirebaseHosting || (Deploy.Target == Target.Terraform && !NeedsAzure);

    /// <summary>A site host reached with its own command line and a token kept as a secret.</summary>
    [JsonIgnore]
    public bool IsTokenHost => Deploy.Target is Target.Netlify or Target.Vercel or Target.CloudflarePages or Target.DigitalOcean or Target.Heroku or Target.ServiceFabric;

    /// <summary>An Azure service deployed with a few az commands of the tool's own, from files of the repository.</summary>
    [JsonIgnore]
    public bool IsAzureCommands => Deploy.Target is Target.ApiManagement or Target.LogicApp or Target.DataFactory;

    /// <summary>The cloud deploys the source of the repository itself, so nothing is packaged.</summary>
    [JsonIgnore]
    public bool DeploysSource => Deploy.Target is Target.GcpCloudFunctions or Target.GcpAppEngine or Target.AwsSam or Target.DigitalOcean or Target.Heroku
        || IsAzureCommands || (Deploy.Target == Target.Database && Deploy.Migrations == MigrationKind.SqlScripts);

    [JsonIgnore]
    public bool IsInfrastructure => Deploy.Target is Target.Terraform or Target.Bicep or Target.ArmTemplate or Target.CloudFormation or Target.AwsCdk or Target.Pulumi;

    /// <summary>The application targets receive the folder out/app built by the pipeline (a script with nothing to build does not).</summary>
    [JsonIgnore]
    public bool UsesPackage => !Build.TestsOnly && !IsContainer && !IsInfrastructure && !DeploysSource
        && !(Deploy.Target == Target.Script && Build.Stack == Stack.None && Build.CustomCommands.Count == 0);

    /// <summary>The build job runs on Windows, so its steps are PowerShell instead of bash.</summary>
    [JsonIgnore]
    public bool IsWindowsBuild => Build.Agent is BuildAgent.HostedWindows or BuildAgent.SelfHostedWindows;

    /// <summary>The deploy runs on the environment's own Windows server, through the runner or agent installed there.</summary>
    [JsonIgnore]
    public bool IsServer => IsServerApp || (Deploy.Target == Target.Script && Deploy.ScriptOnServer);

    /// <summary>An application on your own servers, which keep the previous version next to the current one.</summary>
    [JsonIgnore]
    public bool IsServerApp => IsWindowsApp || Deploy.Target == Target.LinuxService;

    /// <summary>The deploy steps are Windows PowerShell (Windows servers); otherwise bash.</summary>
    [JsonIgnore]
    public bool UsesPowerShell => IsWindowsApp || (Deploy.Target == Target.Script && Deploy.ScriptOnServer);

    /// <summary>The servers have no runner: a hosted agent copies the build to them and runs the deploy over SSH or WinRM.</summary>
    [JsonIgnore]
    public bool IsRemote => IsServerApp && Deploy.Remote;

    /// <summary>The deploy job runs on the runner or agent installed on the environment's servers.</summary>
    [JsonIgnore]
    public bool OnServerRunner => IsServer && !IsRemote;

    /// <summary>The deploy signs in to Azure (everything except Static Web Apps, servers and scripts that do not ask for it).</summary>
    [JsonIgnore]
    public bool NeedsAzure => Deploy.Target switch
    {
        Target.StaticWebApp or Target.WindowsService or Target.Iis or Target.ScheduledTask or Target.LinuxService or Target.Package => false,
        _ when IsCloudCli => false,
        Target.Script => Deploy.ScriptSignsInToAzure && !Deploy.ScriptOnServer,
        _ => true,
    };

    /// <summary>With tags, the tags that deploy: a name or a prefix followed by * (v*).</summary>
    public string TagPattern { get; set; } = "v*";

    /// <summary>When a run fails, post a message to the webhook kept in the secret NOTIFY_WEBHOOK (Slack-style JSON text).</summary>
    public bool NotifyOnFailure { get; set; }

    /// <summary>When the last environment is deployed, post a message to the same webhook (NOTIFY_WEBHOOK).</summary>
    public bool NotifyOnSuccess { get; set; }

    /// <summary>With tags: after the last environment, publish a release for the tag with notes written from the commits (GitHub and GitLab).</summary>
    public bool CreateRelease { get; set; }

    /// <summary>
    /// The repository holds several applications: each one gets files named after it (and its own path filters), so
    /// the pipelines do not overwrite each other.
    /// </summary>
    public bool SeveralApps { get; set; }

    /// <summary>The environments deploy one after another from the same push or tag.</summary>
    [JsonIgnore]
    public bool Promotes => Flow != Flow.BranchPerEnvironment;

    [JsonIgnore]
    public bool IsWindowsApp => Deploy.Target is Target.WindowsService or Target.Iis or Target.ScheduledTask;

    public string BranchOf(EnvironmentSpec environment) => Promotes ? MainBranch : environment.Branch;

    /// <summary>The branches that deploy, without repeats, in environment order.</summary>
    public IReadOnlyList<string> DeployBranches() => Environments.Select(BranchOf).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>A service the tests use, started in a container next to the build.</summary>
public enum ServiceKind
{
    Postgres,
    MySql,
    SqlServer,
    Redis,
    MongoDb,
}

public sealed class BuildSettings
{
    public Stack Stack { get; set; } = Stack.DotNet;

    /// <summary>Major or major.minor: 10.0 for .NET, 22 for Node, 3.12 for Python, 21 for Java. Empty uses the default.</summary>
    public string? Version { get; set; }

    /// <summary>The folder with package.json, requirements.txt or pom.xml; .NET paths are from the repository root.</summary>
    public string WorkingDirectory { get; set; } = ".";

    /// <summary>.NET: the project to publish.</summary>
    public string? ProjectPath { get; set; }

    /// <summary>.NET: the solution or test project to build and test; empty uses the project.</summary>
    public string? TestPath { get; set; }

    public bool RunTests { get; set; } = true;

    /// <summary>Node: the folder the build writes, inside the working directory.</summary>
    public string OutputFolder { get; set; } = "dist";

    /// <summary>When not empty, these commands replace the build; they must leave what is deployed in out/app.</summary>
    public List<string> CustomCommands { get; set; } = [];

    /// <summary>.NET and Java: keep the test results (the Tests tab on Azure DevOps and GitLab, a download on GitHub).</summary>
    public bool PublishTestResults { get; set; }

    /// <summary>.NET, Node.js and Python: fail the build when a package has a known high or critical vulnerability.</summary>
    public bool AuditDependencies { get; set; }

    /// <summary>Keep the downloaded packages between runs (.NET, Node.js, Python and Java on hosted Linux agents).</summary>
    public bool CachePackages { get; set; }

    /// <summary>.NET: collect code coverage with the tests (the test projects reference coverlet.collector).</summary>
    public bool Coverage { get; set; }

    /// <summary>.NET and Java: analyse the code with SonarQube under this project key (SONAR_HOST_URL and the secret SONAR_TOKEN say where).</summary>
    public string? SonarProjectKey { get; set; }

    /// <summary>Analyse the code with Semgrep's public rules and stop on a finding.</summary>
    public bool CodeScan { get; set; }

    /// <summary>GitHub Actions: analyse the code with CodeQL and show what it finds under Security → Code scanning.</summary>
    public bool CodeQl { get; set; }

    /// <summary>Run the language's formatter and linter before the build (.NET, Node.js, Python, Go, Rust, Ruby).</summary>
    public bool Lint { get; set; }

    /// <summary>Scan the repository with Trivy: packages with known high or critical vulnerabilities, secrets in files, risky Dockerfile and infrastructure settings.</summary>
    public bool SecurityScan { get; set; }

    /// <summary>Keep a list of everything the build is made of (a CycloneDX SBOM) with the run.</summary>
    public bool Sbom { get; set; }

    /// <summary>Android and iOS: the platform's own project, React Native or .NET MAUI.</summary>
    public MobileFramework MobileFramework { get; set; } = MobileFramework.Native;

    /// <summary>iOS: the scheme to test and archive.</summary>
    public string? Scheme { get; set; }

    /// <summary>iOS: the export options file (method, team and provisioning profile) of the repository.</summary>
    public string ExportOptions { get; set; } = "ExportOptions.plist";

    public BuildAgent Agent { get; set; } = BuildAgent.HostedLinux;

    /// <summary>Your own agents: the GitHub runner label, the Azure DevOps agent pool or the GitLab runner tag.</summary>
    public string? AgentLabel { get; set; }

    /// <summary>Private NuGet feeds and npm registries the build restores from (Azure Artifacts, GitHub Packages, JFrog...).</summary>
    public List<PackageFeed> PackageFeeds { get; set; } = [];

    /// <summary>Databases and caches started next to the build for the tests; the tests find them with POSTGRES_HOST, POSTGRES_PORT and the like.</summary>
    public List<ServiceKind> Services { get; set; } = [];

    /// <summary>Other versions of the language the tests also run with (a job each); the deploy waits for all of them.</summary>
    public List<string> AlsoTestWith { get; set; } = [];

    /// <summary>A copy of the build that only compiles and tests with another version.</summary>
    [JsonIgnore]
    public bool TestsOnly { get; set; }

    [JsonIgnore]
    public string EffectiveVersion => string.IsNullOrWhiteSpace(Version) ? DefaultVersion(Stack) : Version.Trim();

    /// <summary>The tests can write JUnit XML, which every system shows as test results.</summary>
    [JsonIgnore]
    public bool WritesJUnit => Stack is Stack.Java or Stack.Gradle or Stack.Android or Stack.Scala or Stack.Go or Stack.Php or Stack.Swift or Stack.Cpp
        || (Stack is Stack.Node or Stack.Python or Stack.Rust or Stack.Flutter or Stack.Ruby && Agent is not (BuildAgent.HostedWindows or BuildAgent.SelfHostedWindows));

    /// <summary>Java on Maven or Gradle: the build leaves a jar.</summary>
    [JsonIgnore]
    public bool IsJvm => Stack is Stack.Java or Stack.Gradle;

    public static string DefaultVersion(Stack stack) => stack switch
    {
        Stack.DotNet => "10.0",
        Stack.Node => "22",
        Stack.Python => "3.12",
        Stack.Java or Stack.Gradle or Stack.Scala => "21",
        Stack.Swift => "6.0",
        Stack.Elixir => "1.18",
        Stack.Flutter => "stable",
        Stack.Android => "17",
        Stack.Go => "1.25",
        Stack.Ruby => "3.4",
        Stack.Php => "8.4",
        Stack.Rust => "stable",
        _ => "",
    };
}

public sealed class DeploySettings
{
    public Target Target { get; set; } = Target.AppService;

    /// <summary>App Service and Function App only.</summary>
    public HostOs Os { get; set; } = HostOs.Linux;

    // Containers (Container Apps and AKS): the image is built in Azure Container Registry with az acr build.
    public string? Registry { get; set; }
    public string? ImageName { get; set; }
    public string Dockerfile { get; set; } = "Dockerfile";
    public string DockerContext { get; set; } = ".";

    /// <summary>Images pushed with Docker (ECR, Docker Hub, ghcr.io, another registry): sign them with cosign and the key in the secret COSIGN_PRIVATE_KEY.</summary>
    public bool SignImage { get; set; }

    /// <summary>The stores: the Android package name, the iOS bundle identifier or the Microsoft Store product ID.</summary>
    public string? AppId { get; set; }

    /// <summary>Your own Kubernetes cluster and the registry target: where the image is kept.</summary>
    public RegistryKind RegistryKind { get; set; } = RegistryKind.DockerHub;

    /// <summary>Kubernetes: a Helm chart of the repository (charts/my-app). The deployment name is the release, and an environment's parameters file its values file.</summary>
    public string? HelmChart { get; set; }

    // AKS
    public string Namespace { get; set; } = "default";
    public string Manifests { get; set; } = "k8s";
    public string? DeploymentName { get; set; }
    public string? ContainerName { get; set; }

    /// <summary>The cluster signs in with Microsoft Entra ID, so kubectl needs kubelogin.</summary>
    public bool EntraIdCluster { get; set; } = true;

    // Terraform
    public string InfraDirectory { get; set; } = "infra";
    public string TerraformVersion { get; set; } = "1.9.8";
    public string? StateResourceGroup { get; set; }
    public string? StateStorageAccount { get; set; }
    public string StateContainer { get; set; } = "tfstate";

    /// <summary>Bicep, ARM and CloudFormation: the template file.</summary>
    public string BicepTemplate { get; set; } = "infra/main.bicep";

    /// <summary>App Service and Functions (with a slot), Container Apps, Kubernetes with manifests, Cloud Run and Lambda.</summary>
    public DeployStrategy Strategy { get; set; } = DeployStrategy.Standard;

    /// <summary>Canary: the share of the traffic the new version gets first.</summary>
    public int CanaryPercent { get; set; } = 10;

    /// <summary>Canary: how long the new version keeps that share before it gets everything.</summary>
    public int CanaryMinutes { get; set; } = 5;

    /// <summary>TestFlight: after the last environment, send the build to App Store review.</summary>
    public bool SubmitForReview { get; set; }

    /// <summary>GitOps: the repository with the manifests, without the scheme (github.com/contoso/deployments).</summary>
    public string? GitOpsRepository { get; set; }

    /// <summary>Terraform and Pulumi: the cloud the code manages.</summary>
    public CloudProvider InfraCloud { get; set; } = CloudProvider.Azure;

    /// <summary>Terraform plans and Bicep what-if before changing anything, and the log shows it.</summary>
    public bool Preview { get; set; } = true;

    // Windows servers (Windows service, IIS): the runner or agent on each server runs the deploy in Windows PowerShell.

    /// <summary>Files and folders on the server the copy never touches (appsettings.Production.json, logs, App_Data).</summary>
    public List<string> KeepOnServer { get; set; } = [];

    /// <summary>How long the service or application pool may take to stop or start, and the site to answer.</summary>
    public int StopTimeoutSeconds { get; set; } = 120;

    /// <summary>Windows service: when it does not exist yet, create it running this program of the folder (Billing.Worker.exe).</summary>
    public string? ServiceExecutable { get; set; }

    /// <summary>Windows service and IIS: the file of the build the environment's settings are written into (appsettings.json or Web.config).</summary>
    public string SettingsFile { get; set; } = "appsettings.json";

    // AWS and Google Cloud

    /// <summary>The region (us-east-1 on AWS, us-central1 on Google Cloud).</summary>
    public string? CloudRegion { get; set; }

    /// <summary>Elastic Beanstalk: the S3 bucket that keeps the application versions.</summary>
    public string? Bucket { get; set; }

    /// <summary>Cloud Functions: the runtime (dotnet8, nodejs22, python312, java21) and the entry point.</summary>
    public string? FunctionRuntime { get; set; }

    public string? FunctionEntryPoint { get; set; }

    /// <summary>App Engine: the app.yaml to deploy.</summary>
    public string AppYaml { get; set; } = "app.yaml";

    // Database

    public MigrationKind Migrations { get; set; } = MigrationKind.None;

    /// <summary>EF Core: the project with the migrations (the project to publish is the start-up project).</summary>
    public string? MigrationsProject { get; set; }

    /// <summary>SQL scripts: the folder of the repository with the .sql files.</summary>
    public string MigrationsFolder { get; set; } = "db/migrations";

    /// <summary>Dacpac: the .dacpac file inside the build (Database.dacpac).</summary>
    public string? DacpacPath { get; set; }

    /// <summary>SQL scripts on a server: connect as the runner's Windows account instead of a SQL login.</summary>
    public bool SqlTrustedConnection { get; set; }

    /// <summary>
    /// Servers without a runner: a hosted agent reaches each host of the environment over SSH (Linux) or WinRM over
    /// HTTPS (Windows), copies the build and runs the same deploy there.
    /// </summary>
    public bool Remote { get; set; }

    /// <summary>Windows service, IIS and scheduled task: also write a pipeline started by hand that puts the previous version back.</summary>
    public bool RollbackPipeline { get; set; }

    /// <summary>Service Fabric: the application project (.sfproj) the build packages into out/app.</summary>
    public string? FabricProject { get; set; }

    /// <summary>Every pull request gets its own copy of the application, removed when it closes: a slot of App Service (GitHub Actions and GitLab) or a revision of Container Apps (GitHub Actions, with blue-green or canary).</summary>
    public bool PreviewPullRequests { get; set; }

    /// <summary>Package: the feed the package is published to (NuGet for .NET, npm for Node.js).</summary>
    public PackageFeed? PublishFeed { get; set; }

    // Script

    /// <summary>The deploy commands, one per line; DEPLOY_ENVIRONMENT holds the environment's name.</summary>
    public List<string> DeployCommands { get; set; } = [];

    /// <summary>Run the commands on the environment's server (PowerShell) instead of a hosted runner (bash).</summary>
    public bool ScriptOnServer { get; set; }

    /// <summary>On a hosted runner, sign in to Azure first (the Azure CLI is ready).</summary>
    public bool ScriptSignsInToAzure { get; set; }
}

public sealed class EnvironmentSpec
{
    public string Name { get; set; } = "";

    /// <summary>Used only when each environment has its own branch.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>Someone approves before this environment changes.</summary>
    public bool RequireApproval { get; set; }

    public string? ResourceGroup { get; set; }

    /// <summary>The web app, function app, static web app, container app or AKS cluster.</summary>
    public string? ResourceName { get; set; }

    /// <summary>App Service and Function App: a deployment slot (empty deploys to production).</summary>
    public string? Slot { get; set; }

    /// <summary>Azure DevOps: the Azure Resource Manager service connection for this environment.</summary>
    public string? ServiceConnection { get; set; }

    /// <summary>Terraform: a .tfvars file inside the infrastructure folder. Bicep: a .bicepparam or .json parameters file.</summary>
    public string? ParametersFile { get; set; }

    /// <summary>
    /// Servers: the label of this environment's runners on GitHub, the tag of its virtual machines in the Azure DevOps
    /// environment, or the tag of its GitLab runners.
    /// </summary>
    public string? ServerLabel { get; set; }

    /// <summary>Windows service: its folder (empty uses the folder of the installed service). IIS: the site folder (empty uses the site's path).</summary>
    public string? TargetPath { get; set; }

    /// <summary>IIS: the application pool to stop and start (empty uses the site's own pool).</summary>
    public string? AppPool { get; set; }

    /// <summary>Windows service and IIS: after the start this address must answer with a success code, or the deploy rolls back.</summary>
    public string? HealthCheckUrl { get; set; }

    /// <summary>Remote deploys: the addresses of this environment's servers (app1.contoso.com), updated one after another.</summary>
    public List<string> Hosts { get; set; } = [];

    /// <summary>Servers: when there are several, one runner label (GitHub) or runner tag (GitLab) per server; they deploy one after another.</summary>
    public List<string> Servers { get; set; } = [];

    /// <summary>
    /// Values this environment gets: app settings on App Service, Functions and Container Apps, entries written into the
    /// settings file on Windows services and IIS, and environment variables for your own deploy commands.
    /// </summary>
    public List<EnvironmentSetting> Settings { get; set; } = [];

    /// <summary>Commands before the deploy step, one per line (bash on hosted runners, PowerShell on servers).</summary>
    public List<string> BeforeDeploy { get; set; } = [];

    /// <summary>Commands after a successful deploy, one per line.</summary>
    public List<string> AfterDeploy { get; set; } = [];

    /// <summary>After the deploy, run the Playwright tests of this folder of the repository against the environment.</summary>
    public string? EndToEndFolder { get; set; }

    /// <summary>After the deploy, run this k6 script of the repository against the environment.</summary>
    public string? LoadTestScript { get; set; }

    /// <summary>When this environment may change, in UTC: days of the week and hours, as "1-5 08-18" (Monday to Friday, from 08:00 to 18:00). Empty: always.</summary>
    public string? DeployWindow { get; set; }
}

/// <summary>One value of an environment: written here, or taken from a secret of the pipeline system.</summary>
public sealed class EnvironmentSetting
{
    /// <summary>The setting, with : between levels (ConnectionStrings:Default, Logging:LogLevel:Default).</summary>
    public string Name { get; set; } = "";

    /// <summary>The value, when it is not a secret.</summary>
    public string? Value { get; set; }

    /// <summary>The name of the secret that holds the value (DB_CONNECTION); the value itself never appears in the YAML.</summary>
    public string? Secret { get; set; }
}

public enum Severity
{
    Error,
    Warning,
}

public sealed record Message(Severity Severity, string Text);

public sealed record GeneratedFile(string Path, string Content);

public sealed record GenerationResult(IReadOnlyList<GeneratedFile> Files, IReadOnlyList<Message> Messages)
{
    public bool HasErrors => Messages.Any(message => message.Severity == Severity.Error);
}
