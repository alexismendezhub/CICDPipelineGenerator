using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CICDPipelineGenerator.Domain;

/// <summary>What the AI returns: the whole proposed spec, what it assumed, and what it needs the person to answer.</summary>
public sealed class PipelineDraft
{
    public string Summary { get; set; } = "";
    public PipelineSpec Spec { get; set; } = new();
    public List<ReviewItem> Review { get; set; } = [];
    public List<string> Questions { get; set; } = [];
}

/// <summary>The part of the screen a review item is about.</summary>
public enum ReviewArea
{
    Project,
    Platforms,
    Build,
    Deploy,
    Environments,
    Triggers,
}

public sealed record ReviewItem(ReviewArea Area, string Text);

/// <summary>The instructions and the output schema for the AI that proposes pipeline settings from a description.</summary>
public static class ProposalPrompt
{
    public const int MaxDescription = 4000;

    public static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string System() =>
        $$"""
        You set up deployment pipelines in CI/CD Pipeline Generator, a tool that writes the YAML for GitHub Actions, Azure DevOps and GitLab CI/CD.
        The person describes in their own words what they want to deploy and how; you return the complete settings as JSON in the required schema.
        They review your proposal and approve or correct it before anything changes, so never guess silently: every value you chose that the description does not state goes in "review".

        What the settings mean:
        - name: the project, lowercase letters, digits and dashes, starting with a letter (my-app). repository: owner/name on GitHub or group/project on GitLab, or "" when unknown.
        - platforms: the pipeline systems to write (gitHubActions, azureDevOps, gitLab). When none is named, use gitHubActions and add a review item.
        - deploy.target: appService (web app or API on Azure App Service), functionApp (Azure Functions), staticWebApp (a static site or SPA on Azure Static Web Apps), containerApp (a Docker image on Azure Container Apps), aks (a Docker image on Azure Kubernetes Service with manifests), terraform or bicep (infrastructure as code only), windowsService (a Windows service on the company's own servers), iis (an IIS site on the company's own servers), script (the person's own deploy commands).
        - Own servers (windowsService, iis): the runner or agent on each server is already installed. keepOnServer lists file and folder names the copy never touches (appsettings.Production.json, logs, App_Data...; [] when not said, with a review item); stopTimeoutSeconds 120 unless said; serviceExecutable (windowsService only: the .exe inside the folder, set only when the service must be created when missing, otherwise ""). Per environment: resourceName is the Windows service name or the IIS site name, serverLabel the label or tag of its servers (required), targetPath the folder such as D:\Apps\Billing ("" uses the installed service's or site's folder; required when serviceExecutable is set), appPool (iis only, "" uses the site's pool), healthCheckUrl (an http(s) address that must answer after the start, "" for none).
        - script: deployCommands, one command per entry (bash on a hosted runner, or PowerShell when scriptOnServer is true; DEPLOY_ENVIRONMENT holds the environment name); scriptSignsInToAzure (hosted runners only) signs in to Azure first. On a server, serverLabel is required per environment.
        - Database: deploy.migrations is none (default), efCoreBundle (Entity Framework Core migrations: migrationsProject is the project that has them; needs a dotNet build) or sqlScripts (every .sql file of migrationsFolder, default db/migrations, run in name order with sqlcmd). sqlTrustedConnection: true only for sqlScripts on their own servers when they say Windows authentication. The connection is always a secret (DB_CONNECTION, or DB_SERVER, DB_NAME, DB_USER, DB_PASSWORD), never part of the settings.
        - Per environment, settings: values the environment gets (app settings on appService, functionApp and containerApp; entries written into deploy.settingsFile, default appsettings.json or Web.config for .NET Framework, on windowsService and iis; environment variables for script). Each has name (levels separated by :, such as ConnectionStrings:Default), and either value (plain text, one line, no $) or secret (the NAME in capitals of a secret of the pipeline system, with value ""). Passwords, keys and connection strings are always secrets; never write a secret value. [] unless asked.
        - Per environment, servers: when an environment has several servers, one runner label or tag per server ([] for one server, where serverLabel is enough); they are updated one after another.
        - Per environment, beforeDeploy and afterDeploy: commands the person wants right before the deploy step and after it succeeds, one per entry ([] unless asked); bash on hosted runners (sh in the GitLab Terraform image), PowerShell on servers.
        - deploy.os: linux or windows, only for appService and functionApp (linux unless asked).
        - Containers (containerApp, aks): registry is the Azure Container Registry name without .azurecr.io (letters and digits); imageName lowercase; dockerfile and dockerContext are paths (Dockerfile and .).
        - AKS: namespace, manifests (folder or file, default k8s), deploymentName and containerName (lowercase, digits, dashes), entraIdCluster (true unless told the cluster uses local accounts).
        - build.mobileFramework: native (default), reactNative or maui, only for android and ios (maui: projectPath is the .csproj; reactNative: workingDirectory is the app's root, with its android and ios folders).
        - Application stores: googlePlay (build.stack android: a signed bundle; deploy.appId is the package name; each environment's resourceName is its track: internal, alpha, beta or production), appStore (build.stack ios on build.agent hostedMac: projectPath is the .xcworkspace or .xcodeproj, build.scheme the scheme, build.exportOptions the export options file, default ExportOptions.plist; appId is the bundle identifier; resourceName an optional TestFlight group) and microsoftStore (build.stack msix on a Windows agent: projectPath is the packaging project; appId is the Store product ID; resourceName an optional flight ID). For every other target appId and scheme are "" and exportOptions is ExportOptions.plist.
        - build.lint (the language's formatter and linter: dotNet, node, python, go, rust, ruby), build.securityScan (Trivy: vulnerable packages, secrets in files, risky Dockerfile and infrastructure settings) and build.sbom (keep a CycloneDX list of components): false unless asked, Linux build agents only. deploy.signImage: sign the pushed image with cosign (awsEcs, awsEks, kubernetes, containerRegistry only), false unless asked.
        - serviceFabric (the application package in out/app is uploaded to each environment's cluster and the application created or upgraded; resourceName is the application name, appId the application type name, fabricProject the .sfproj path; build.agent hostedWindows).
        - database (only the migrations run; deploy.migrations must not be none), apiManagement (bicepTemplate is the OpenAPI file, appId the API's identifier, resourceName the API Management service), logicApp (infraDirectory is the workflows folder, resourceName the Logic App) and dataFactory (bicepTemplate is the ARM template the factory publishes, resourceName the factory), all with resourceGroup. heroku (a Dockerfile; resourceName is the Heroku app).
        - build.codeQl: analyse the code with CodeQL (gitHubActions only; false unless asked). build.codeScan: analyse the code with Semgrep (false unless asked). deploy.submitForReview: after the last TestFlight environment, send the build to App Store review (false unless asked). environments[].endToEndFolder (a folder with Playwright tests) and environments[].loadTestScript (a k6 script) run after that environment's deploy; "" unless asked.
        - digitalOcean: an App Platform app that builds from the repository itself (resourceName is the app ID); the pipeline starts its deployment.
        - Site hosts (the built site in out/app): netlify (resourceName is the site), vercel (resourceName is the project ID), cloudflarePages (resourceName is the project) and firebaseHosting (resourceName is the Firebase project). awsSam (infraDirectory is the folder with template.yaml, resourceName the stack, cloudRegion), awsAppRunner (a Docker image in ECR; resourceName is the service, imageName the ECR repository, cloudRegion) and gitOps (the image goes to deploy.registryKind and its tag is written into deploy.gitOpsRepository, such as github.com/contoso/deployments, in each environment's parametersFile; Argo CD or Flux does the rest).
        - terraform on AWS or Google Cloud: deploy.infraCloud aws or gcp, deploy.bucket the bucket that keeps the state (and cloudRegion for aws); the state* fields are only for azure.
        - environments[].deployWindow: "" or the days of the week and hours in UTC the environment may change, as "1-5 08-18"; only when asked.
        - platforms: gitHubActions, azureDevOps, gitLab, jenkins (a declarative Jenkinsfile), bitbucket (bitbucket-pipelines.yml), circleCi (.circleci/config.yml), travisCi (.travis.yml), cloudBuild (cloudbuild.yaml), codePipeline (CodeBuild buildspec files), teamCity (.teamcity/settings.kts) and argoWorkflows (a WorkflowTemplate); choose only the ones the person names.
        - Kubernetes anywhere: awsEks (resourceName is the EKS cluster; the image goes to ECR, imageName is the ECR repository; cloudRegion), kubernetes (any cluster, reached with a kubeconfig secret; no resource names) and containerRegistry (only builds and pushes the image; each environment tags it). kubernetes and containerRegistry keep the image in deploy.registryKind dockerHub (registry is the user or organisation), gitHub (ghcr.io: registry is the owner) or other (registry is the address, such as registry.contoso.com/team); for every other target registryKind is dockerHub.
        - deploy.helmChart: the folder of a Helm chart in the repository (charts/my-app) when a Kubernetes target (aks, gcpGke, awsEks, kubernetes) deploys with Helm: deploymentName is then the release name and an environment's parametersFile its values file; "" deploys with manifests and kubectl.
        - Terraform: infraDirectory (default infra), terraformVersion a full release (default 1.9.8), stateResourceGroup, stateStorageAccount (3 to 24 lowercase letters and digits), stateContainer (default tfstate).
        - Bicep: bicepTemplate (default infra/main.bicep).
        - Other infrastructure targets (stack none): armTemplate (an ARM JSON template in bicepTemplate, deployed into each resourceGroup), cloudFormation (bicepTemplate is the template file, resourceName the stack, parametersFile an optional JSON parameters file, cloudRegion), awsCdk (infraDirectory is the folder with cdk.json, resourceName the CDK stack, cloudRegion) and pulumi (infraDirectory is the folder with Pulumi.yaml, resourceName the Pulumi stack such as dev, deploy.infraCloud azure, aws or gcp is the cloud it manages; cloudRegion for aws). infraCloud is azure for every other target.
        - deploy.strategy: standard (default), blueGreen or canary, only when asked and only for appService and functionApp (the environments need a slot), containerApp, the Kubernetes targets without Helm, gcpCloudRun and awsLambda. canaryPercent (default 10) is the share of traffic the new version gets first and canaryMinutes (default 5) how long; with a strategy, an environment's healthCheckUrl is checked before the new version gets all the traffic.
        - deploy.preview: terraform plan, Bicep or ARM what-if, a CloudFormation change set, cdk diff or pulumi preview before applying (true unless asked not to).
        - build.agent: hostedLinux (default), hostedWindows, selfHostedLinux, selfHostedWindows, hostedMac or selfHostedMac (macOS only for ios); for the self-hosted ones agentLabel is the GitHub runner label, Azure DevOps agent pool or GitLab runner tag ("" otherwise). Windows agents build dotNet, dotNetFramework or custom commands (PowerShell) and never container images.
        - build.stack dotNetFramework: .NET Framework 4.x, built with MSBuild on a Windows agent (hostedWindows unless they have their own); projectPath is the project to publish and testPath the solution.
        - build.stack: dotNet, node, python, java (Maven), gradle (Java or Kotlin with Gradle), go, ruby (Bundler), php (Composer), rust (Cargo), cpp (C or C++ with CMake), swift (server-side Swift), scala (sbt), elixir (Mix), flutter (a Flutter web app) or none (nothing to compile: a Dockerfile only, infrastructure, or custom commands). For terraform and bicep use none.
        - build.version: "" for the default (.NET 10.0, Node 22, Python 3.12, Java 21) or a number such as 8.0 or 20.
        - build.workingDirectory: the folder with package.json, requirements.txt or pom.xml ("." is the root). .NET paths are from the root: projectPath is the project to publish (required for .NET apps that are not containers), testPath the solution or test project ("" uses the project).
        - build.outputFolder: Node only, the folder the build creates (dist, build, out...).
        - build.packageFeeds: private feeds the build restores from, [] unless the person mentions private packages. Each one: name (letters, digits, underscores: company), type (nuGet for .NET builds, npm for Node builds), kind (azureArtifacts, gitHubPackages, jFrog or other), organization (Azure DevOps organization, or the GitHub owner), project (Azure Artifacts project-scoped feed, "" otherwise), feed (Azure Artifacts feed name, or the JFrog repository key), url (JFrog address such as https://company.jfrog.io, or the full URL for other; "" otherwise), scope (npm scope such as @company, required for gitHubPackages npm as @owner; "" for nuGet), inRepositoryConfig (true only when the person says the repository's nuget.config or .npmrc already lists it), jFrogOidcProvider (the JFrog OpenID Connect integration name for GitHub Actions; "" signs in with a token). Credentials are never part of the settings: the setup guide says which secrets to create.
        - build.services: databases and caches the tests need, started next to the build (postgres, mySql, sqlServer, redis, mongoDb); [] unless the description says the tests use one. build.alsoTestWith: other language versions the tests also run with (like "20" or "3.12"); [] unless asked.
        - setupScript: true (with gitHubActions, azureDevOps or gitLab and Azure, a script that does the one-time setup is written too).
        - templateRepository: the repository that keeps shared pipeline templates (owner/name), only when the person asks for templates shared between repositories; null otherwise. templateRef: its branch, "main" unless said.
        - build.runTests: true unless asked not to. build.customCommands: one shell command per entry that replaces the build and leaves what is deployed in out/app; [] unless the person gives commands.
        - flow: promotion (one branch, mainBranch, deploys every environment in order, each after the one before), branchPerEnvironment (each environment deploys from its own branch) or tag (a tag matching tagPattern, default v*, deploys every environment in order; pushes to mainBranch only build).
        - notifyOnFailure: true when they want to hear about failed runs (a webhook kept in the secret NOTIFY_WEBHOOK). notifyOnSuccess: true when they also want a message once the last environment is deployed. createRelease: true when a tag should also publish a release with notes (flow tag; GitHub and GitLab).
        - build.publishTestResults (dotNet and java only), build.auditDependencies (fail on known high or critical vulnerabilities: dotNet, node, python), build.cachePackages (keep downloaded packages between runs: hosted Linux only): false unless asked.
        - AWS targets (deploy.cloudRegion such as us-east-1): awsS3 (a static site synchronised to a bucket; resourceName is the bucket), awsLambda (resourceName is the function), awsElasticBeanstalk (resourceGroup is the application, resourceName the environment, deploy.bucket the S3 bucket for versions), awsEcs (a Docker image: resourceGroup is the cluster, resourceName the service, imageName the ECR repository, containerName the container of the task definition).
        - Google Cloud targets (deploy.cloudRegion such as us-central1): gcpCloudRun (a Docker image: resourceName is the service, registry the Artifact Registry repository, imageName the image), gcpCloudFunctions (resourceName is the function, functionRuntime such as dotnet8, nodejs22 or python312, functionEntryPoint; deployed from build.workingDirectory), gcpAppEngine (deploy.appYaml, default app.yaml), gcpGke (as aks: resourceName is the cluster, registry, imageName, namespace, manifests, deploymentName, containerName).
        - Cloud names never go in resourceGroup unless said above ("" otherwise). Sign-in to AWS and Google Cloud is set up in SETUP.md, never in the settings.
        - deploy.target linuxService: a systemd service on their own Linux servers (resourceName is the service name, targetPath its folder such as /opt/billing, "" uses the service's WorkingDirectory; keepOnServer, healthCheckUrl, serverLabel and servers as for windowsService).
        - deploy.remote: true when the servers have no runner or agent and must be reached from outside (SSH for linuxService, WinRM over HTTPS for the Windows targets); then each environment lists hosts (server addresses) instead of serverLabel. false unless they say so.
        - build.coverage (dotNet only: code coverage with the tests) and build.sonarProjectKey (dotNet and java: the SonarQube project key, "" for none).
        - deploy.target scheduledTask: a Windows scheduled task on their own servers (resourceName is the task name; the rest as windowsService).
        - deploy.migrations dacpac: a .dacpac inside the build published with sqlpackage; dacpacPath is its path inside the build (Database.dacpac).
        - deploy.target package: publishes a NuGet (dotNet) or npm (node) package to deploy.publishFeed, which has the same fields as a package feed (name "publish"); for every other target fill publishFeed with name "publish", type nuGet, kind other and "" everywhere.
        - deploy.previewPullRequests: true when every pull request should get its own copy of the application (appService on gitHubActions and gitLab; containerApp on gitHubActions with strategy blueGreen or canary); false unless asked.
        - deploy.rollbackPipeline: true when they want a pipeline started by hand that puts the previous version back (windowsService, iis, scheduledTask only).
        - severalApps: true when the repository holds several applications that deploy separately; each gets its own files, and pathFilters should name this application's folders.
        - Package feeds also serve gradle and android (type maven: the build declares a repository named like the feed with credentials(PasswordCredentials)), go (type go), ruby (type rubyGems), php (type composer) and rust (type cargo), on jFrog or other (rubyGems also on gitHubPackages).
        - Package feeds also serve java (type maven; gitHubPackages needs feed = the repository) and python (type pip; not on gitHubPackages).
        - environments, in deployment order: name (lowercase, digits, dashes: dev, test, staging, prod), branch (used only with branchPerEnvironment), requireApproval, resourceGroup, resourceName (the web app, function app, static web app, container app or AKS cluster; "" for terraform and bicep), slot (appService or functionApp only, "" for none), serviceConnection (the Azure DevOps service connection; required when azureDevOps is chosen, otherwise ""), parametersFile (terraform .tfvars inside infraDirectory, or a Bicep parameters file; "" for none and for other targets).
        - buildPullRequests: pull requests build and test but never deploy (true unless asked). manualRun: can be started by hand (true unless asked). pathFilters: only run when these paths change ([] unless asked). schedule: a cron expression with five fields in UTC when the person wants it to run at set times (0 6 * * 1-5), "" otherwise.

        Rules:
        - Names and paths never contain spaces, quotes, semicolons or other shell characters.
        - When environments are not named, use dev and prod, with approval on prod, and add a review item. The last environment should need approval unless the person says otherwise.
        - When resource names are not given, propose names that follow the Azure naming convention (app-<name>-<env>, func-<name>-<env>, ca-<name>-<env>, aks-<name>-<env>, rg-<name>-<env>) and add one review item saying they are proposals to confirm.
        - Azure DevOps service connections that are not given are azure-<env>, with a review item.
        - When a current configuration is given, keep everything the person does not ask to change and return the complete settings, not only the changes.
        - Put what CI/CD Pipeline Generator cannot do (other clouds than Azure, AWS and Google Cloud...; your own commands in a script target can still do it) and questions the person should answer in "questions". Never invent a target or a stack.
        - Write "summary", "review" and "questions" in the language the person used. Keep each item to one short sentence.
        """;

    public static string User(string description, PipelineSpec? current) =>
        current is null
            ? $"Description:\n{description.Trim()}\n\nThere are no current settings: start new ones."
            : $"Description:\n{description.Trim()}\n\nCurrent settings (keep what still fits):\n{JsonSerializer.Serialize(current, Json)}";

    /// <summary>The JSON schema of PipelineDraft for structured outputs: every object closed and every field required.</summary>
    public static JsonObject Schema()
    {
        static JsonObject Obj(JsonObject properties) => new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(properties.Select(pair => (JsonNode)pair.Key).ToArray()),
            ["additionalProperties"] = false,
        };
        static JsonObject Str() => new() { ["type"] = "string" };
        static JsonObject Bool() => new() { ["type"] = "boolean" };
        static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };
        static JsonObject Choice<T>() where T : struct, Enum => new()
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(global::System.Enum.GetNames<T>().Select(name => (JsonNode)JsonNamingPolicy.CamelCase.ConvertName(name)).ToArray()),
        };

        return Obj(new JsonObject
        {
            ["summary"] = Str(),
            ["spec"] = Obj(new JsonObject
            {
                ["name"] = Str(),
                ["repository"] = Str(),
                ["platforms"] = Array(Choice<Platform>()),
                ["build"] = Obj(new JsonObject
                {
                    ["stack"] = Choice<Stack>(),
                    ["version"] = Str(),
                    ["workingDirectory"] = Str(),
                    ["projectPath"] = Str(),
                    ["testPath"] = Str(),
                    ["runTests"] = Bool(),
                    ["outputFolder"] = Str(),
                    ["customCommands"] = Array(Str()),
                    ["publishTestResults"] = Bool(),
                    ["auditDependencies"] = Bool(),
                    ["cachePackages"] = Bool(),
                    ["coverage"] = Bool(),
                    ["sonarProjectKey"] = Str(),
                    ["codeScan"] = Bool(),
                    ["codeQl"] = Bool(),
                    ["lint"] = Bool(),
                    ["securityScan"] = Bool(),
                    ["sbom"] = Bool(),
                    ["services"] = Array(Choice<ServiceKind>()),
                    ["alsoTestWith"] = Array(Str()),
                    ["mobileFramework"] = Choice<MobileFramework>(),
                    ["scheme"] = Str(),
                    ["exportOptions"] = Str(),
                    ["agent"] = Choice<BuildAgent>(),
                    ["agentLabel"] = Str(),
                    ["packageFeeds"] = Array(Obj(new JsonObject
                    {
                        ["name"] = Str(),
                        ["type"] = Choice<FeedType>(),
                        ["kind"] = Choice<FeedKind>(),
                        ["organization"] = Str(),
                        ["project"] = Str(),
                        ["feed"] = Str(),
                        ["url"] = Str(),
                        ["scope"] = Str(),
                        ["inRepositoryConfig"] = Bool(),
                        ["jFrogOidcProvider"] = Str(),
                    })),
                }),
                ["deploy"] = Obj(new JsonObject
                {
                    ["target"] = Choice<Target>(),
                    ["os"] = Choice<HostOs>(),
                    ["registry"] = Str(),
                    ["imageName"] = Str(),
                    ["dockerfile"] = Str(),
                    ["dockerContext"] = Str(),
                    ["namespace"] = Str(),
                    ["manifests"] = Str(),
                    ["deploymentName"] = Str(),
                    ["containerName"] = Str(),
                    ["entraIdCluster"] = Bool(),
                    ["appId"] = Str(),
                    ["signImage"] = Bool(),
                    ["submitForReview"] = Bool(),
                    ["gitOpsRepository"] = Str(),
                    ["registryKind"] = Choice<RegistryKind>(),
                    ["helmChart"] = Str(),
                    ["infraDirectory"] = Str(),
                    ["terraformVersion"] = Str(),
                    ["stateResourceGroup"] = Str(),
                    ["stateStorageAccount"] = Str(),
                    ["stateContainer"] = Str(),
                    ["bicepTemplate"] = Str(),
                    ["infraCloud"] = Choice<CloudProvider>(),
                    ["strategy"] = Choice<DeployStrategy>(),
                    ["canaryPercent"] = new JsonObject { ["type"] = "integer" },
                    ["canaryMinutes"] = new JsonObject { ["type"] = "integer" },
                    ["preview"] = Bool(),
                    ["keepOnServer"] = Array(Str()),
                    ["stopTimeoutSeconds"] = new JsonObject { ["type"] = "integer" },
                    ["serviceExecutable"] = Str(),
                    ["deployCommands"] = Array(Str()),
                    ["scriptOnServer"] = Bool(),
                    ["scriptSignsInToAzure"] = Bool(),
                    ["settingsFile"] = Str(),
                    ["migrations"] = Choice<MigrationKind>(),
                    ["migrationsProject"] = Str(),
                    ["migrationsFolder"] = Str(),
                    ["dacpacPath"] = Str(),
                    ["cloudRegion"] = Str(),
                    ["bucket"] = Str(),
                    ["functionRuntime"] = Str(),
                    ["functionEntryPoint"] = Str(),
                    ["appYaml"] = Str(),
                    ["rollbackPipeline"] = Bool(),
                    ["previewPullRequests"] = Bool(),
                    ["fabricProject"] = Str(),
                    ["remote"] = Bool(),
                    ["publishFeed"] = Obj(new JsonObject
                {
                    ["name"] = Str(),
                    ["type"] = Choice<FeedType>(),
                    ["kind"] = Choice<FeedKind>(),
                    ["organization"] = Str(),
                    ["project"] = Str(),
                    ["feed"] = Str(),
                    ["url"] = Str(),
                    ["scope"] = Str(),
                    ["inRepositoryConfig"] = Bool(),
                    ["jFrogOidcProvider"] = Str(),
                }),
                    ["sqlTrustedConnection"] = Bool(),
                }),
                ["flow"] = Choice<Flow>(),
                ["mainBranch"] = Str(),
                ["buildPullRequests"] = Bool(),
                ["manualRun"] = Bool(),
                ["pathFilters"] = Array(Str()),
                ["schedule"] = Str(),
                ["setupScript"] = Bool(),
                ["templateRepository"] = Str(),
                ["templateRef"] = Str(),
                ["tagPattern"] = Str(),
                ["severalApps"] = Bool(),
                ["notifyOnFailure"] = Bool(),
                ["notifyOnSuccess"] = Bool(),
                ["createRelease"] = Bool(),
                ["environments"] = Array(Obj(new JsonObject
                {
                    ["name"] = Str(),
                    ["branch"] = Str(),
                    ["requireApproval"] = Bool(),
                    ["resourceGroup"] = Str(),
                    ["resourceName"] = Str(),
                    ["slot"] = Str(),
                    ["serviceConnection"] = Str(),
                    ["parametersFile"] = Str(),
                    ["serverLabel"] = Str(),
                    ["targetPath"] = Str(),
                    ["appPool"] = Str(),
                    ["healthCheckUrl"] = Str(),
                    ["deployWindow"] = Str(),
                    ["endToEndFolder"] = Str(),
                    ["loadTestScript"] = Str(),
                    ["beforeDeploy"] = Array(Str()),
                    ["afterDeploy"] = Array(Str()),
                    ["servers"] = Array(Str()),
                    ["hosts"] = Array(Str()),
                    ["settings"] = Array(Obj(new JsonObject { ["name"] = Str(), ["value"] = Str(), ["secret"] = Str() })),
                })),
            }),
            ["review"] = Array(Obj(new JsonObject { ["area"] = Choice<ReviewArea>(), ["text"] = Str() })),
            ["questions"] = Array(Str()),
        });
    }

    public static PipelineDraft Parse(string json) =>
        JsonSerializer.Deserialize<PipelineDraft>(json, Json) ?? throw new JsonException("The AI returned an empty proposal.");
}
