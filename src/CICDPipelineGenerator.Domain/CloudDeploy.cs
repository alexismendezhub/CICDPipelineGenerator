namespace CICDPipelineGenerator.Domain;

/// <summary>
/// AWS and Google Cloud: the commands of their command lines every pipeline system shares, and how each system signs
/// in. GitHub and GitLab sign in with OpenID Connect (a role on AWS, workload identity federation on Google Cloud);
/// Azure DevOps has no such sign-in for them without extensions, so it reads an access key or a service account key
/// kept as secret variables.
/// </summary>
public static class CloudDeploy
{
    public const string AwsImage = "python:3.12-slim";

    /// <summary>GitLab's hosted macOS runners: their tag and the image with Xcode.</summary>
    public const string MacImage = "macos-15-xcode-16";
    public const string MacTag = "saas-macos-medium-m1";

    // The registry of the account the pipeline signed in to.
    private const string EcrRegistry = "registry=\"`aws sts get-caller-identity --query Account --output text`.dkr.ecr.$AWS_REGION.amazonaws.com\"";

    /// <summary>The registry of a generic image as Docker names it, and the address docker login takes (none for Docker Hub).</summary>
    public static (string Prefix, string Login) Registry(DeploySettings deploy) => deploy.RegistryKind switch
    {
        RegistryKind.DockerHub => ($"docker.io/{deploy.Registry}", ""),
        RegistryKind.GitHub => ($"ghcr.io/{deploy.Registry}", " ghcr.io"),
        _ => (deploy.Registry ?? "", " " + (deploy.Registry ?? "").Split('/')[0]),
    };

    public const string RegistryUser = "REGISTRY_USER";
    public const string RegistryPassword = "REGISTRY_PASSWORD";

    private static string DockerLogin(PipelineSpec spec) =>
        $"printf '%s' \"${RegistryPassword}\" | docker login{Registry(spec.Deploy).Login} --username \"${RegistryUser}\" --password-stdin";

    public static string Image(PipelineSpec spec, string tag) => spec.IsGenericRegistry
        ? $"{Registry(spec.Deploy).Prefix}/{spec.Deploy.ImageName}:{tag}"
        : spec.IsAws
        ? $"$registry/{spec.Deploy.ImageName}:{tag}"
        : $"{spec.Deploy.CloudRegion}-docker.pkg.dev/$GCP_PROJECT_ID/{spec.Deploy.Registry}/{spec.Deploy.ImageName}:{tag}";

    /// <summary>AWS builds with Docker and pushes to ECR; Google Cloud builds in Cloud Build (the Dockerfile of the build folder).</summary>
    public static string BuildImage(PipelineSpec spec, string tag) => spec.IsGenericRegistry
        ? string.Join('\n',
        [
            DockerLogin(spec),
            $"docker build -t \"{Image(spec, tag)}\" -f {spec.Deploy.Dockerfile} {spec.Deploy.DockerContext}",
            $"docker push \"{Image(spec, tag)}\"",
            spec.Deploy.SignImage ? Quality.Sign(Image(spec, tag)) : "",
        ]).TrimEnd('\n')
        : spec.IsAws
        ? string.Join('\n',
        [
            EcrRegistry,
            "aws ecr get-login-password | docker login --username AWS --password-stdin \"$registry\"",
            $"docker build -t \"{Image(spec, tag)}\" -f {spec.Deploy.Dockerfile} {spec.Deploy.DockerContext}",
            $"docker push \"{Image(spec, tag)}\"",
            spec.Deploy.SignImage ? Quality.Sign(Image(spec, tag)) : "",
        ]).TrimEnd('\n')
        : $"gcloud builds submit {spec.Deploy.DockerContext} --tag \"{Image(spec, tag)}\" --quiet";

    /// <summary>The deploy of one environment; tag is the commit as the pipeline system writes it.</summary>
    public static string Deploy(PipelineSpec spec, EnvironmentSpec environment, Platform platform, string tag)
    {
        var deploy = spec.Deploy;
        var package = platform == Platform.AzureDevOps ? "$PIPELINE_WORKSPACE/app" : Commands.PackageFolder;
        var name = environment.ResourceName;
        var region = deploy.CloudRegion;
        switch (deploy.Target)
        {
            case Target.AwsS3:
                return $"aws s3 sync \"{package}\" \"s3://{name}\" --delete";
            case Target.AwsLambda:
                return string.Join('\n',
                [
                    $"python3 -m zipfile -c app.zip \"{package}\"/.",
                    $"aws lambda update-function-code --function-name {name} --zip-file fileb://app.zip --query LastUpdateStatus --output text",
                    $"aws lambda wait function-updated --function-name {name}",
                    deploy.Strategy == DeployStrategy.Standard ? "" : Strategies.Lambda(spec, environment),
                ]).TrimEnd('\n');
            case Target.AwsElasticBeanstalk:
                return string.Join('\n',
                [
                    $"version=\"{spec.Name}-{tag}\"",
                    $"python3 -m zipfile -c app.zip \"{package}\"/.",
                    $"aws s3 cp app.zip \"s3://{deploy.Bucket}/{spec.Name}/$version.zip\"",
                    $"aws elasticbeanstalk create-application-version --application-name {environment.ResourceGroup} --version-label \"$version\" --source-bundle \"S3Bucket={deploy.Bucket},S3Key={spec.Name}/$version.zip\" --query ApplicationVersion.VersionLabel --output text",
                    deploy.Strategy == DeployStrategy.BlueGreen && !string.IsNullOrWhiteSpace(environment.Slot)
                        ? Strategies.BeanstalkSwap(environment)
                        : $"aws elasticbeanstalk update-environment --environment-name {name} --version-label \"$version\" --query Status --output text\naws elasticbeanstalk wait environment-updated --environment-names {name}",
                ]);
            case Target.AwsEcs:
                return string.Join('\n',
                [
                    EcrRegistry,
                    $"export IMAGE=\"{Image(spec, tag)}\"",
                    $"task=`aws ecs describe-services --cluster {environment.ResourceGroup} --services {name} --query 'services[0].taskDefinition' --output text`",
                    "aws ecs describe-task-definition --task-definition \"$task\" --query taskDefinition > task.json",
                    // The same task definition with the new image; the fields AWS fills in itself are left out.
                    "python3 - <<'PY'",
                    "import json, os",
                    "with open(\"task.json\") as file:",
                    "    task = json.load(file)",
                    "for key in (\"taskDefinitionArn\", \"revision\", \"status\", \"requiresAttributes\", \"compatibilities\", \"registeredAt\", \"registeredBy\", \"deregisteredAt\"):",
                    "    task.pop(key, None)",
                    "for container in task[\"containerDefinitions\"]:",
                    $"    if container[\"name\"] == \"{deploy.ContainerName}\":",
                    "        container[\"image\"] = os.environ[\"IMAGE\"]",
                    "with open(\"task.json\", \"w\") as file:",
                    "    json.dump(task, file)",
                    "PY",
                    "new=`aws ecs register-task-definition --cli-input-json file://task.json --query taskDefinition.taskDefinitionArn --output text`",
                    deploy.Strategy != DeployStrategy.Standard
                        ? Strategies.Ecs(spec, environment)
                        : $"aws ecs update-service --cluster {environment.ResourceGroup} --service {name} --task-definition \"$new\" --query service.serviceName --output text\naws ecs wait services-stable --cluster {environment.ResourceGroup} --services {name}",
                ]);
            case Target.ServiceFabric:
                return string.Join('\n',
                [
                    "python3 -m pip install --quiet --break-system-packages sfctl 2> /dev/null || python3 -m pip install --quiet sfctl",
                    "printf '%s' \"$SF_CLIENT_CERT\" > \"$HOME/sf-client.pem\"",
                    "sfctl cluster select --endpoint \"$SF_CLUSTER_ENDPOINT\" --pem \"$HOME/sf-client.pem\" --no-verify",
                    // The package says its own version, so every run gives it the commit's.
                    $"version=\"{tag}\"",
                    $"sed -i -E \"s/(ApplicationTypeVersion=\\\")[^\\\"]*/\\\\1$version/\" \"{package}/ApplicationManifest.xml\"",
                    // The values of this environment: ApplicationParameters/<environment>.xml next to the project, when the repository has it.
                    "params=$(python3 - <<'PY'",
                    "import glob, json, os",
                    "import xml.etree.ElementTree as ET",
                    $"files = [path for path in glob.glob(\"{FabricFolder(spec)}ApplicationParameters/*.xml\") if os.path.splitext(os.path.basename(path))[0].lower() == \"{environment.Name}\"]",
                    "values = {}",
                    "for parameter in (ET.parse(files[0]).getroot().iter(\"{http://schemas.microsoft.com/2011/01/fabric}Parameter\") if files else []):",
                    "    values[parameter.get(\"Name\")] = parameter.get(\"Value\")",
                    "print(json.dumps(values))",
                    "PY",
                    ")",
                    $"sfctl application upload --path \"{package}\"",
                    "sfctl application provision --application-type-build-path app",
                    $"if sfctl application info --application-id {name} 2> /dev/null | grep -q '\"name\"'; then",
                    // A monitored upgrade goes back to the version before when the cluster reports it unhealthy.
                    $"  sfctl application upgrade --application-id {name} --application-version \"$version\" --parameters \"$params\" --mode Monitored --failure-action Rollback",
                    "else",
                    $"  sfctl application create --app-name fabric:/{name} --app-type {deploy.AppId} --app-version \"$version\" --parameters \"$params\"",
                    "fi",
                ]);
            case Target.Netlify:
                return $"npx --yes netlify-cli deploy --prod --dir \"{package}\" --site {name}";
            case Target.Vercel:
                return $"npx --yes vercel deploy \"{package}\" --prod --yes --token \"$VERCEL_TOKEN\"";
            case Target.CloudflarePages:
                return $"npx --yes wrangler pages deploy \"{package}\" --project-name {name}";
            case Target.Database:
                return $"echo \"The database of {environment.Name} is up to date.\"";
            case Target.Heroku:
                return string.Join('\n',
                [
                    "command -v curl > /dev/null || apk add --no-cache curl > /dev/null",
                    "printf '%s' \"$HEROKU_API_KEY\" | docker login --username=_ --password-stdin registry.heroku.com",
                    $"docker build -t registry.heroku.com/{name}/web -f {deploy.Dockerfile} {deploy.DockerContext}",
                    $"docker push registry.heroku.com/{name}/web",
                    $"image=$(docker inspect registry.heroku.com/{name}/web --format '{{{{.Id}}}}')",
                    // Heroku runs the pushed image when the app's formation is told its ID.
                    $"curl -fsS -X PATCH https://api.heroku.com/apps/{name}/formation -H \"Content-Type: application/json\" -H \"Accept: application/vnd.heroku+json; version=3.docker-releases\" -H \"Authorization: Bearer $HEROKU_API_KEY\" -d \"{{\\\"updates\\\":[{{\\\"type\\\":\\\"web\\\",\\\"docker_image\\\":\\\"$image\\\"}}]}}\" > /dev/null",
                ]);
            case Target.DigitalOcean:
                return string.Join('\n',
                [
                    "mkdir -p \"$HOME/.local/bin\"; export PATH=\"$HOME/.local/bin:$PATH\"",
                    "command -v doctl > /dev/null || curl -fsSL https://github.com/digitalocean/doctl/releases/download/v2.7.0/doctl-2.7.0-linux-amd64.tar.gz | tar -xz -C \"$HOME/.local/bin\"",
                    // App Platform builds the commit itself; this starts the deployment and waits until it is live.
                    $"doctl apps create-deployment {name} --wait",
                ]);
            case Target.FirebaseHosting:
                // firebase.json names the folder to publish: the build is in out/app.
                return $"npx --yes firebase-tools deploy --only hosting --project {name} --non-interactive";
            case Target.AwsSam:
                return string.Join('\n',
                [
                    "python3 -m venv /tmp/sam",
                    "/tmp/sam/bin/pip install --quiet aws-sam-cli",
                    $"cd {deploy.InfraDirectory}",
                    "/tmp/sam/bin/sam build",
                    $"/tmp/sam/bin/sam deploy --stack-name {name} --resolve-s3 --capabilities CAPABILITY_IAM --no-confirm-changeset --no-fail-on-empty-changeset --region \"$AWS_REGION\"",
                ]);
            case Target.AwsAppRunner:
                return string.Join('\n',
                [
                    EcrRegistry,
                    $"arn=$(aws apprunner list-services --query \"ServiceSummaryList[?ServiceName=='{name}'].ServiceArn | [0]\" --output text)",
                    $"aws apprunner update-service --service-arn \"$arn\" --source-configuration \"ImageRepository={{ImageIdentifier={Image(spec, tag)},ImageRepositoryType=ECR}}\" --query Service.Status --output text",
                    "for attempt in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30 31 32 33 34 35 36 37 38 39 40; do",
                    "  status=$(aws apprunner describe-service --service-arn \"$arn\" --query Service.Status --output text)",
                    "  if [ \"$status\" != OPERATION_IN_PROGRESS ]; then break; fi",
                    "  sleep 15",
                    "done",
                    "if [ \"$status\" != RUNNING ]; then echo \"The service is $status after the update.\"; exit 1; fi",
                ]);
            case Target.GitOps:
            {
                // The manifests repository gets the new tag; Argo CD or Flux sees the commit and updates the cluster.
                var image = Image(spec, tag);
                var repository = image[..image.LastIndexOf(':')];
                return string.Join('\n',
                [
                    $"git clone --depth 1 \"https://x-access-token:$GITOPS_TOKEN@{deploy.GitOpsRepository}\" \"$HOME/gitops\"",
                    "cd \"$HOME/gitops\"",
                    $"sed -i -E \"s#({repository}):[A-Za-z0-9._-]+#\\1:{tag}#g\" {environment.ParametersFile}",
                    "if git diff --quiet; then echo \"The manifests already name this image.\"; exit 0; fi",
                    "git config user.name \"pipeline\"; git config user.email \"pipeline@users.noreply.invalid\"",
                    $"git commit --quiet -am \"Deploy {spec.Name} {tag} to {environment.Name}\"",
                    "git push --quiet",
                ]);
            }

            case Target.CloudFormation or Target.AwsCdk or Target.Pulumi or Target.Terraform:
                return Infra.Deploy(spec, environment, Systems.RunId(platform));
            case Target.GooglePlay or Target.AppStore or Target.MicrosoftStore:
                return Store.Deploy(spec, environment, platform);
            case Target.AwsEks:
                return string.Join('\n',
                [
                    EcrRegistry,
                    Kube.InstallKubectl,
                    $"aws eks update-kubeconfig --name {name} --region \"$AWS_REGION\"",
                    Kube.Rollout(spec, environment, $"\"{Image(spec, tag)}\""),
                ]);
            case Target.Kubernetes:
                return string.Join('\n', [Kube.InstallKubectl, Kube.UseKubeConfig, Kube.Rollout(spec, environment, $"\"{Image(spec, tag)}\"")]);
            case Target.ContainerRegistry:
                // The image of this commit also gets the environment's name as a tag, without pulling it.
                return string.Join('\n',
                [
                    DockerLogin(spec),
                    $"docker buildx imagetools create --tag \"{Image(spec, environment.Name)}\" \"{Image(spec, tag)}\"",
                ]);
            case Target.GcpCloudRun when deploy.Strategy != DeployStrategy.Standard:
                return Strategies.CloudRun(spec, environment, Image(spec, tag));
            case Target.GcpCloudRun:
                return $"gcloud run deploy {name} --image \"{Image(spec, tag)}\" --region {region} --quiet";
            case Target.GcpCloudFunctions:
                return $"gcloud functions deploy {name} --gen2 --region {region} --runtime {deploy.FunctionRuntime} --entry-point {deploy.FunctionEntryPoint} --source {spec.Build.WorkingDirectory} --trigger-http --quiet";
            case Target.GcpAppEngine:
                return $"gcloud app deploy {deploy.AppYaml} --quiet";
            default:
                return string.Join('\n',
                [
                    // Azure DevOps agents may lack the plugin kubectl signs in to GKE with.
                    platform == Platform.AzureDevOps ? "gcloud components install gke-gcloud-auth-plugin --quiet || sudo apt-get install -y -qq google-cloud-cli-gke-gcloud-auth-plugin" : null,
                    $"gcloud container clusters get-credentials {name} --region {region} --quiet",
                    Kube.Rollout(spec, environment, $"\"{Image(spec, tag)}\""),
                ]).Replace("\n\n", "\n", StringComparison.Ordinal).TrimStart('\n');
        }
    }

    /// <summary>The deploy reads files of the repository (the source, app.yaml or the manifests).</summary>
    /// <summary>The folder of the Service Fabric project, where its ApplicationParameters are.</summary>
    private static string FabricFolder(PipelineSpec spec) =>
        spec.Deploy.FabricProject is { } project && project.Replace('\\', '/').LastIndexOf('/') is var slash and >= 0 ? project.Replace('\\', '/')[..(slash + 1)] : "";

    public static bool NeedsRepository(PipelineSpec spec) => spec.Deploy.Target == Target.ServiceFabric || spec.DeploysSource || spec.IsKubernetes || spec.IsInfrastructure || spec.Deploy.Target is Target.MicrosoftStore or Target.FirebaseHosting or Target.Heroku
        || (spec.Deploy.Target == Target.Database && spec.Deploy.Migrations == MigrationKind.SqlScripts);

    /// <summary>The variables the cloud commands need; environment is null for the build job.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Env(PipelineSpec spec, EnvironmentSpec? environment, Platform platform)
    {
        var env = new List<KeyValuePair<string, string>>();
        var scope = environment ?? spec.Environments[0];
        if (environment is null && spec.Deploy.SignImage)
        {
            env.Add(new(Quality.CosignKey, Secrets.Shared(platform, Quality.CosignKey)));
            env.Add(new(Quality.CosignPassword, Secrets.Shared(platform, Quality.CosignPassword)));
        }

        if (spec.IsStore)
        {
            return Store.Env(spec, platform);
        }

        if (spec.IsTokenHost && environment is not null)
        {
            // Each host's command line reads its token (and account) from these variables.
            string[] names = spec.Deploy.Target switch
            {
                Target.Netlify => ["NETLIFY_AUTH_TOKEN"],
                Target.Vercel => ["VERCEL_TOKEN", "VERCEL_ORG_ID"],
                Target.DigitalOcean => ["DIGITALOCEAN_ACCESS_TOKEN"],
                Target.Heroku => ["HEROKU_API_KEY"],
                Target.ServiceFabric => ["SF_CLUSTER_ENDPOINT", "SF_CLIENT_CERT"],
                _ => ["CLOUDFLARE_API_TOKEN", "CLOUDFLARE_ACCOUNT_ID"],
            };

            // A cluster and its certificate belong to one environment; the hosts' tokens are for the whole account.
            env.AddRange(names.Select(name => new KeyValuePair<string, string>(name, spec.Deploy.Target == Target.ServiceFabric ? Secrets.Reference(platform, environment, name) : Secrets.Shared(platform, name))));
            if (spec.Deploy.Target == Target.Vercel)
            {
                env.Add(new("VERCEL_PROJECT_ID", environment.ResourceName ?? ""));
            }

            return env;
        }

        if (spec.Deploy.Target == Target.GitOps && environment is not null)
        {
            env.Add(new("GITOPS_TOKEN", Secrets.Shared(platform, "GITOPS_TOKEN")));
            return env;
        }

        if (spec.IsGenericRegistry)
        {
            // The build pushes the image and the registry target tags it; the cluster only needs its kubeconfig.
            if (environment is null || spec.Deploy.Target == Target.ContainerRegistry)
            {
                var gitHubPackages = platform == Platform.GitHubActions && spec.Deploy.RegistryKind == RegistryKind.GitHub;
                env.Add(new(RegistryUser, gitHubPackages ? "${{ github.actor }}" : Secrets.Shared(platform, RegistryUser)));
                env.Add(new(RegistryPassword, gitHubPackages ? "${{ secrets.GITHUB_TOKEN }}" : Secrets.Shared(platform, RegistryPassword)));
            }
            else
            {
                env.Add(new("KUBE_CONFIG", Secrets.Reference(platform, scope, "KUBE_CONFIG")));
            }

            return env;
        }

        if (spec.Deploy.Target == Target.Pulumi)
        {
            env.Add(new(Infra.PulumiToken, Secrets.Shared(platform, Infra.PulumiToken)));
            if (spec.NeedsAzure && platform == Platform.GitHubActions)
            {
                env.AddRange(Infra.GitHubAzureEnv);
            }
        }

        if (spec.IsAws)
        {
            env.Add(new("AWS_REGION", spec.Deploy.CloudRegion ?? ""));
            env.Add(new("AWS_DEFAULT_REGION", spec.Deploy.CloudRegion ?? ""));
            if (platform == Platform.AzureDevOps)
            {
                env.Add(new("AWS_ACCESS_KEY_ID", Secrets.Reference(platform, scope, "AWS_ACCESS_KEY_ID")));
                env.Add(new("AWS_SECRET_ACCESS_KEY", Secrets.Reference(platform, scope, "AWS_SECRET_ACCESS_KEY")));
            }
        }
        else if (spec.IsGcp)
        {
            switch (platform)
            {
                case Platform.GitHubActions:
                    env.Add(new("GCP_PROJECT_ID", "${{ vars.GCP_PROJECT_ID }}"));
                    break;
                case Platform.AzureDevOps:
                    env.Add(new("GCP_PROJECT_ID", Secrets.Reference(platform, scope, "GCP_PROJECT_ID")));
                    env.Add(new("GCP_CREDENTIALS", Secrets.Reference(platform, scope, "GCP_CREDENTIALS")));
                    break;
            }

            if (spec.Deploy.Target == Target.GcpGke)
            {
                env.Add(new("USE_GKE_GCLOUD_AUTH_PLUGIN", "True"));
            }
        }

        return env;
    }

    /// <summary>Azure DevOps: the lines that sign in before the commands (AWS needs none: the keys are in the environment).</summary>
    public static string AzureDevOpsSignIn(PipelineSpec spec) => !spec.IsGcp
        ? ""
        : string.Join('\n',
        [
            "printf '%s' \"$GCP_CREDENTIALS\" > \"$AGENT_TEMPDIRECTORY/gcp.json\"",
            "export GOOGLE_APPLICATION_CREDENTIALS=\"$AGENT_TEMPDIRECTORY/gcp.json\"",
            "gcloud auth activate-service-account --key-file=\"$AGENT_TEMPDIRECTORY/gcp.json\" --quiet",
            "gcloud config set project \"$GCP_PROJECT_ID\" --quiet",
        ]) + "\n";

    /// <summary>GitLab: the lines that exchange the job's ID token for cloud credentials.</summary>
    public static IReadOnlyList<string> GitLabSignIn(PipelineSpec spec, bool docker = false) => spec.SignsInWithLibraries && !docker
        ? spec.IsAws
            ?
            [
                // The AWS SDK exchanges the job's token for the role by itself: no command line to install.
                "printf '%s' \"$AWS_JWT\" > \"$CI_BUILDS_DIR/.aws-jwt\"",
                "export AWS_WEB_IDENTITY_TOKEN_FILE=\"$CI_BUILDS_DIR/.aws-jwt\" AWS_ROLE_SESSION_NAME=\"gitlab-$CI_PIPELINE_ID\"",
            ]
            :
            [
                // The credential file gcloud would write, for the Google libraries to exchange the job's token.
                "printf '%s' \"$GCP_JWT\" > \"$CI_BUILDS_DIR/.gcp-jwt\"",
                ExternalAccount("$CI_BUILDS_DIR/.gcp-jwt", "$CI_BUILDS_DIR/.gcp.json"),
                "export GOOGLE_APPLICATION_CREDENTIALS=\"$CI_BUILDS_DIR/.gcp.json\"",
            ]
        : spec.IsAws
        ?
        [
            docker ? "apk add --no-cache aws-cli > /dev/null" : "pip install --quiet awscli",
            "export $(printf \"AWS_ACCESS_KEY_ID=%s AWS_SECRET_ACCESS_KEY=%s AWS_SESSION_TOKEN=%s\" $(aws sts assume-role-with-web-identity --role-arn \"$AWS_ROLE_ARN\" --role-session-name \"gitlab-$CI_PIPELINE_ID\" --web-identity-token \"$AWS_JWT\" --duration-seconds 3600 --query 'Credentials.[AccessKeyId,SecretAccessKey,SessionToken]' --output text))",
        ]
        :
        [
            "echo \"$GCP_JWT\" > \"$CI_BUILDS_DIR/.gcp-jwt\"",
            "gcloud iam workload-identity-pools create-cred-config \"$GCP_WORKLOAD_IDENTITY_PROVIDER\" --service-account=\"$GCP_SERVICE_ACCOUNT\" --credential-source-file=\"$CI_BUILDS_DIR/.gcp-jwt\" --output-file=\"$CI_BUILDS_DIR/.gcp.json\"",
            "gcloud auth login --cred-file=\"$CI_BUILDS_DIR/.gcp.json\" --quiet",
            "gcloud config set project \"$GCP_PROJECT_ID\" --quiet",
        ];

    /// <summary>
    /// Writes the credential file gcloud would write for workload identity federation: the Google libraries (and gcloud)
    /// read it and exchange the token kept in tokenFile for the service account's credentials.
    /// </summary>
    public static string ExternalAccount(string tokenFile, string outFile) =>
        "printf '{\"type\":\"external_account\",\"audience\":\"//iam.googleapis.com/%s\",\"subject_token_type\":\"urn:ietf:params:oauth:token-type:jwt\",\"token_url\":\"https://sts.googleapis.com/v1/token\",\"credential_source\":{\"file\":\"%s\"},\"service_account_impersonation_url\":\"https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/%s:generateAccessToken\"}' "
        + $"\"$GCP_WORKLOAD_IDENTITY_PROVIDER\" \"{tokenFile}\" \"$GCP_SERVICE_ACCOUNT\" > \"{outFile}\"";

    /// <summary>GitLab: the ID token the sign-in exchanges.</summary>
    public static KeyValuePair<string, string> GitLabToken(PipelineSpec spec) => spec.IsAws
        ? new("AWS_JWT", "https://gitlab.com")
        : new("GCP_JWT", "https://iam.googleapis.com/$GCP_WORKLOAD_IDENTITY_PROVIDER");

    public static string GitLabImage(PipelineSpec spec) => spec.Deploy.Target == Target.Heroku
        ? "docker:27"
        : spec.Deploy.Target == Target.Database
        ? Commands.BuildImage(spec)
        : spec.Deploy.Target == Target.ServiceFabric
        ? "python:3.12"
        : spec.IsTokenHost || spec.Deploy.Target is Target.FirebaseHosting or Target.GitOps
        ? "node:22"
        : spec.IsStore
        ? spec.Deploy.Target == Target.GooglePlay ? "ruby:3.4" : spec.Deploy.Target == Target.AppStore ? MacImage : "mcr.microsoft.com/dotnet/sdk:10.0"
        : spec.IsInfrastructure
        ? Infra.Image(spec)
        : spec.Deploy.Target == Target.ContainerRegistry
        ? "docker:27"
        : spec.IsAws || spec.IsGenericRegistry
        ? AwsImage
        : spec.Deploy.Target == Target.GcpGke ? "google/cloud-sdk:latest" : "google/cloud-sdk:slim";
}
