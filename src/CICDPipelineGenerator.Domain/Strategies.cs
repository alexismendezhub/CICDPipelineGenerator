namespace CICDPipelineGenerator.Domain;

/// <summary>
/// Blue-green and canary deploys, written with each target's own way of splitting traffic: App Service slots,
/// Container Apps revisions, Kubernetes deployments, Cloud Run revisions and Lambda aliases. The new version never
/// gets all the traffic before it is healthy, and a failed check gives the traffic back to the old one.
/// </summary>
public static class Strategies
{
    /// <summary>The Lambda alias the callers use; its version is what changes.</summary>
    public const string LambdaAlias = "live";

    public static bool Supports(PipelineSpec spec) => spec.Deploy.Target is Target.AppService or Target.FunctionApp or Target.ContainerApp or Target.GcpCloudRun or Target.AwsLambda
        || spec.IsKubernetes
        || spec.Deploy.Target == Target.AwsEcs
        || (spec.Deploy.Target == Target.AwsElasticBeanstalk && spec.Deploy.Strategy == DeployStrategy.BlueGreen)
        || (spec.IsServerApp && spec.Deploy.Strategy == DeployStrategy.Canary);

    /// <summary>
    /// Helm. Blue-green: the releases name-blue and name-green of the same chart, and a service named like the release
    /// whose selector (the label app.kubernetes.io/instance) says which one is live. Canary: the release name-canary first.
    /// </summary>
    public static string Helm(PipelineSpec spec, EnvironmentSpec environment, string image)
    {
        var deploy = spec.Deploy;
        var ns = $"-n {deploy.Namespace}";
        var name = deploy.DeploymentName;
        string Patch(string slot) => $"kubectl patch service {name} {ns} --type merge --patch \"{{\\\"spec\\\":{{\\\"selector\\\":{{\\\"app.kubernetes.io/instance\\\":\\\"{name}-{slot}\\\"}}}}}}\"";
        if (Canary(spec))
        {
            var undo = $"helm rollback {name}-canary {ns} --wait";
            return Lines(
                Kube.InstallHelm,
                $"image={image}",
                Kube.HelmUpgrade(spec, environment, name + "-canary") + $" || {{ {undo}; exit 1; }}",
                $"echo \"The canary release runs the new version for {deploy.CanaryMinutes} minute(s).\"\nsleep {deploy.CanaryMinutes * 60}",
                Check(environment, undo),
                Kube.HelmUpgrade(spec, environment, name!));
        }

        return Lines(
            Kube.InstallHelm,
            $"image={image}",
            $"live=$(kubectl get service {name} {ns} -o jsonpath='{{.spec.selector.app\\.kubernetes\\.io/instance}}' 2> /dev/null || true)",
            $"if [ \"$live\" = \"{name}-blue\" ]; then next=green; previous=blue; else next=blue; previous=green; fi",
            Kube.HelmUpgrade(spec, environment, name + "-$next"),
            Patch("$next"),
            Check(environment, Patch("$previous")),
            "echo \"The traffic moved to the $next release; $previous keeps the previous version.\"");
    }

    /// <summary>Elastic Beanstalk: the version goes to the second environment (written as the slot), and the two swap their addresses.</summary>
    public static string BeanstalkSwap(EnvironmentSpec environment) => Lines(
        $"aws elasticbeanstalk update-environment --environment-name {environment.Slot} --version-label \"$version\" --query Status --output text",
        $"aws elasticbeanstalk wait environment-updated --environment-names {environment.Slot}",
        Check(environment, "true"),
        $"aws elasticbeanstalk swap-environment-cnames --source-environment-name {environment.Slot} --destination-environment-name {environment.ResourceName}",
        $"echo \"{environment.Slot} and {environment.ResourceName} swapped addresses: the previous version stays on the one that is now idle.\"");

    /// <summary>Servers: after the first one is updated, the others wait and it is checked again before they change.</summary>
    public static string? ServerPause(PipelineSpec spec, EnvironmentSpec environment)
    {
        if (!spec.IsServerApp || spec.Deploy.Strategy != DeployStrategy.Canary)
        {
            return null;
        }

        var seconds = spec.Deploy.CanaryMinutes * 60;
        var url = environment.HealthCheckUrl;
        return spec.UsesPowerShell
            ? $"Write-Host 'The first server runs the new version for {spec.Deploy.CanaryMinutes} minute(s) before the others change.'; Start-Sleep -Seconds {seconds}" + (string.IsNullOrWhiteSpace(url) ? "" : $"; Invoke-WebRequest -UseBasicParsing -Uri '{url}' | Out-Null")
            : $"echo \"The first server runs the new version for {spec.Deploy.CanaryMinutes} minute(s) before the others change.\"; sleep {seconds}" + (string.IsNullOrWhiteSpace(url) ? "" : $"; curl -fsS \"{url}\" > /dev/null");
    }

    /// <summary>The first of the environment's servers, when it has several.</summary>
    public static string? FirstServer(PipelineSpec spec, EnvironmentSpec environment) =>
        spec.IsRemote ? (environment.Hosts.Count > 1 ? environment.Hosts[0] : null) : environment.Servers.Count > 1 ? environment.Servers[0] : null;

    private static bool Canary(PipelineSpec spec) => spec.Deploy.Strategy == DeployStrategy.Canary;

    /// <summary>
    /// ECS with CodeDeploy: the service's deployment controller is CODE_DEPLOY, and a CodeDeploy application and
    /// deployment group named like the service move the load balancer from the old tasks to the new ones, all at once
    /// (blue-green) or a tenth first (canary). $new is the task definition just registered.
    /// </summary>
    public static string Ecs(PipelineSpec spec, EnvironmentSpec environment)
    {
        var name = environment.ResourceName;
        var config = !Canary(spec) ? "CodeDeployDefault.ECSAllAtOnce"
            : spec.Deploy.CanaryMinutes <= 5 ? "CodeDeployDefault.ECSCanary10Percent5Minutes" : "CodeDeployDefault.ECSCanary10Percent15Minutes";
        return Lines(
            "export NEW_TASK=\"$new\"",
            "python3 - <<'PY'",
            "import json, os",
            "with open(\"task.json\") as file:",
            "    task = json.load(file)",
            $"container = next(item for item in task[\"containerDefinitions\"] if item[\"name\"] == \"{spec.Deploy.ContainerName}\")",
            "spec = {\"version\": 1, \"Resources\": [{\"TargetService\": {\"Type\": \"AWS::ECS::Service\", \"Properties\": {",
            "    \"TaskDefinition\": os.environ[\"NEW_TASK\"],",
            $"    \"LoadBalancerInfo\": {{\"ContainerName\": \"{spec.Deploy.ContainerName}\", \"ContainerPort\": container[\"portMappings\"][0][\"containerPort\"]}}}}}}}}]}}",
            "with open(\"revision.json\", \"w\") as file:",
            "    json.dump({\"revisionType\": \"AppSpecContent\", \"appSpecContent\": {\"content\": json.dumps(spec)}}, file)",
            "PY",
            $"id=`aws deploy create-deployment --application-name {name} --deployment-group-name {name} --deployment-config-name {config} --revision file://revision.json --query deploymentId --output text`",
            // When the new tasks are not healthy, CodeDeploy leaves the traffic on the old ones and this fails.
            "aws deploy wait deployment-successful --deployment-id \"$id\"");
    }

    private static string Wait(PipelineSpec spec) => $"echo \"The new version keeps {spec.Deploy.CanaryPercent}% of the traffic for {spec.Deploy.CanaryMinutes} minute(s).\"\nsleep {spec.Deploy.CanaryMinutes * 60}";

    /// <summary>The environment's health address must answer, or the commands in undo run and the deploy fails.</summary>
    private static string? Check(EnvironmentSpec environment, string undo) => string.IsNullOrWhiteSpace(environment.HealthCheckUrl)
        ? null
        : $"curl -fsS --retry 5 --retry-delay 10 --retry-all-errors \"{environment.HealthCheckUrl}\" > /dev/null || {{ {undo}; echo \"The health check failed: the previous version keeps the traffic.\"; exit 1; }}";

    private static string Lines(params string?[] lines) => string.Join('\n', lines.Where(line => !string.IsNullOrEmpty(line)));

    /// <summary>
    /// App Service and Functions: the build is already in the slot. Canary sends it a share of the production traffic
    /// first; then the slot is swapped into production, which keeps the previous version in the slot.
    /// </summary>
    public static string? SlotSwap(PipelineSpec spec, EnvironmentSpec environment)
    {
        if (spec.Deploy.Strategy == DeployStrategy.Standard || spec.Deploy.Target is not (Target.AppService or Target.FunctionApp) || string.IsNullOrWhiteSpace(environment.Slot))
        {
            return null;
        }

        var kind = spec.Deploy.Target == Target.AppService ? "webapp" : "functionapp";
        var app = $"--resource-group {environment.ResourceGroup} --name {environment.ResourceName}";
        var clear = $"az webapp traffic-routing clear {app} --output none";
        return Lines(
            Canary(spec) ? $"az webapp traffic-routing set {app} --distribution {environment.Slot}={spec.Deploy.CanaryPercent} --output none" : null,
            Canary(spec) ? Wait(spec) : null,
            Check(environment, Canary(spec) ? clear : "true"),
            $"az {kind} deployment slot swap {app} --slot {environment.Slot} --target-slot production --output none",
            Canary(spec) ? clear : null);
    }

    private const string WaitForHealth = "for attempt in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30; do";

    /// <summary>Container Apps, the app's own update: the command returns while the new revision still starts, so the deploy waits until it is healthy.</summary>
    public static string ContainerAppStandard(EnvironmentSpec environment, string update)
    {
        var app = $"--name {environment.ResourceName} --resource-group {environment.ResourceGroup}";
        return Lines(
            update + " --output none",
            $"new=$(az containerapp show {app} --query properties.latestRevisionName --output tsv)",
            WaitForHealth,
            $"  health=$(az containerapp revision show {app} --revision \"$new\" --query properties.healthState --output tsv)",
            "  if [ \"$health\" = Healthy ]; then break; fi",
            "  sleep 10",
            "done",
            "if [ \"$health\" != Healthy ]; then echo \"The new revision $new is not healthy.\"; exit 1; fi");
    }

    /// <summary>Container Apps: a new revision next to the one that has the traffic, which keeps it until the new one is healthy.</summary>
    public static string ContainerApp(PipelineSpec spec, EnvironmentSpec environment, string update)
    {
        var app = $"--name {environment.ResourceName} --resource-group {environment.ResourceGroup}";
        const string Latest = "--query \"sort_by([?properties.active], &properties.createdTime)[-1].name\" --output tsv";
        var undo = $"az containerapp ingress traffic set {app} --revision-weight \"$old=100\" --output none";
        return Lines(
            $"az containerapp revision set-mode {app} --mode multiple --output none",
            // The revision that has the traffic now (a preview of a pull request is newer and has none); the first time, the latest one.
            $"old=$(az containerapp ingress traffic show {app} --query \"sort_by([?revisionName], &weight)[-1].revisionName\" --output tsv)",
            $"if [ -z \"$old\" ]; then old=$(az containerapp revision list {app} {Latest}); fi",
            // The traffic is tied to the revision that has it, so the new one starts with none.
            $"az containerapp ingress traffic set {app} --revision-weight \"$old=100\" --output none",
            update + " --output none",
            $"new=$(az containerapp show {app} --query properties.latestRevisionName --output tsv)",
            WaitForHealth,
            $"  health=$(az containerapp revision show {app} --revision \"$new\" --query properties.healthState --output tsv)",
            "  if [ \"$health\" = Healthy ]; then break; fi",
            "  sleep 10",
            "done",
            "if [ \"$health\" != Healthy ]; then echo \"The new revision $new is not healthy: $old keeps the traffic.\"; exit 1; fi",
            Canary(spec) ? $"az containerapp ingress traffic set {app} --revision-weight \"$old={100 - spec.Deploy.CanaryPercent}\" \"$new={spec.Deploy.CanaryPercent}\" --output none" : null,
            Canary(spec) ? Wait(spec) : null,
            Check(environment, undo),
            $"az containerapp ingress traffic set {app} --revision-weight \"$new=100\" --output none",
            $"az containerapp revision deactivate {app} --revision \"$old\" --output none");
    }

    /// <summary>
    /// Kubernetes with manifests. Blue-green: the deployments name-blue and name-green, and the service name whose
    /// selector label slot says which one is live. Canary: the deployment name-canary behind the same service.
    /// </summary>
    public static string Kubernetes(PipelineSpec spec, EnvironmentSpec environment, string image)
    {
        var deploy = spec.Deploy;
        var ns = $"-n {deploy.Namespace}";
        var name = deploy.DeploymentName;
        if (Canary(spec))
        {
            var undo = $"kubectl rollout undo {ns} deployment/{name}-canary";
            return Lines(
                $"kubectl apply {ns} -f {deploy.Manifests}",
                $"kubectl set image {ns} deployment/{name}-canary {deploy.ContainerName}={image}",
                $"kubectl rollout status {ns} deployment/{name}-canary --timeout=300s || {{ {undo}; exit 1; }}",
                $"echo \"The canary runs the new version for {deploy.CanaryMinutes} minute(s).\"\nsleep {deploy.CanaryMinutes * 60}",
                Check(environment, undo),
                // A canary that crashed while it waited stops the deploy before the main deployment changes.
                $"kubectl rollout status {ns} deployment/{name}-canary --timeout=60s || {{ {undo}; exit 1; }}",
                $"kubectl set image {ns} deployment/{name} {deploy.ContainerName}={image}",
                $"kubectl rollout status {ns} deployment/{name} --timeout=300s");
        }

        return Lines(
            $"kubectl apply {ns} -f {deploy.Manifests}",
            $"live=$(kubectl get service {name} {ns} -o jsonpath='{{.spec.selector.slot}}')",
            "if [ \"$live\" = blue ]; then next=green; else next=blue; fi",
            $"kubectl set image {ns} deployment/{name}-$next {deploy.ContainerName}={image}",
            $"kubectl rollout status {ns} deployment/{name}-$next --timeout=300s",
            $"kubectl patch service {name} {ns} --patch \"{{\\\"spec\\\":{{\\\"selector\\\":{{\\\"slot\\\":\\\"$next\\\"}}}}}}\"",
            Check(environment, $"kubectl patch service {name} {ns} --patch \"{{\\\"spec\\\":{{\\\"selector\\\":{{\\\"slot\\\":\\\"$live\\\"}}}}}}\""),
            "echo \"The traffic moved from $live to $next; $live keeps the previous version.\"");
    }

    /// <summary>Cloud Run: the new revision is deployed without traffic under the tag candidate, then gets a share or all of it.</summary>
    public static string CloudRun(PipelineSpec spec, EnvironmentSpec environment, string image)
    {
        var service = $"{environment.ResourceName} --region {spec.Deploy.CloudRegion}";
        var undo = $"gcloud run services update-traffic {service} --to-revisions \"$old=100\" --quiet";
        return Lines(
            $"old=$(gcloud run services describe {service} --format='value(status.latestReadyRevisionName)')",
            $"gcloud run deploy {environment.ResourceName} --image \"{image}\" --region {spec.Deploy.CloudRegion} --no-traffic --tag candidate --quiet",
            Canary(spec) ? $"gcloud run services update-traffic {service} --to-tags candidate={spec.Deploy.CanaryPercent} --quiet" : null,
            Canary(spec) ? Wait(spec) : null,
            Check(environment, undo),
            $"gcloud run services update-traffic {service} --to-latest --quiet");
    }

    /// <summary>Lambda: the new code is published as a version, and the alias live moves to it, at once or after sharing the calls.</summary>
    public static string Lambda(PipelineSpec spec, EnvironmentSpec environment)
    {
        var function = $"--function-name {environment.ResourceName}";
        var alias = $"{function} --name {LambdaAlias}";
        var undo = $"aws lambda update-alias {alias} --function-version \"$old\" --routing-config 'AdditionalVersionWeights={{}}' --output text > /dev/null";
        var share = (spec.Deploy.CanaryPercent / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return Lines(
            $"version=$(aws lambda publish-version {function} --query Version --output text)",
            $"old=$(aws lambda get-alias {alias} --query FunctionVersion --output text)",
            Canary(spec) ? $"aws lambda update-alias {alias} --function-version \"$old\" --routing-config \"AdditionalVersionWeights={{$version={share}}}\" --output text > /dev/null" : null,
            Canary(spec) ? Wait(spec) : null,
            Check(environment, undo),
            $"aws lambda update-alias {alias} --function-version \"$version\" --routing-config 'AdditionalVersionWeights={{}}' --output text > /dev/null",
            $"echo \"The alias {LambdaAlias} moved from version $old to $version.\"");
    }
}
