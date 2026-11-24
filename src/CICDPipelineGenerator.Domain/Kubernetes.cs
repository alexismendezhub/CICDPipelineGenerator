namespace CICDPipelineGenerator.Domain;

/// <summary>
/// What every Kubernetes target shares once kubectl can reach the cluster (AKS, GKE, EKS or your own): apply the
/// manifests and change the image, or upgrade a Helm release.
/// </summary>
public static class Kube
{
    public const string KubectlVersion = "v1.33.0";
    public const string HelmVersion = "v3.17.0";
    private const string LocalBin = "mkdir -p \"$HOME/.local/bin\"; export PATH=\"$HOME/.local/bin:$PATH\"";

    /// <summary>Installs kubectl in the home folder when the runner does not have it (Python downloads it: every image used here has Python).</summary>
    public const string InstallKubectl =
        LocalBin + "\ncommand -v kubectl > /dev/null || { python3 -c \"import urllib.request; urllib.request.urlretrieve('https://dl.k8s.io/release/" + KubectlVersion + "/bin/linux/amd64/kubectl', '$HOME/.local/bin/kubectl')\"; chmod +x \"$HOME/.local/bin/kubectl\"; }";

    public const string InstallHelm =
        LocalBin + "\ncommand -v helm > /dev/null || { mkdir -p /tmp/helm; python3 -c \"import tarfile, urllib.request; tarfile.open(fileobj=urllib.request.urlopen('https://get.helm.sh/helm-" + HelmVersion + "-linux-amd64.tar.gz'), mode='r|gz').extractall('/tmp/helm')\"; mv /tmp/helm/linux-amd64/helm \"$HOME/.local/bin/helm\"; }";

    /// <summary>Your own cluster: the kubeconfig of the environment, kept base64-encoded in the secret KUBE_CONFIG.</summary>
    public const string UseKubeConfig =
        "printf '%s' \"$KUBE_CONFIG\" | base64 -d > \"$HOME/.kube-config\"\nchmod 600 \"$HOME/.kube-config\"\nexport KUBECONFIG=\"$HOME/.kube-config\"";

    /// <summary>One helm upgrade of the chart as the given release, with the image in $image.</summary>
    public static string HelmUpgrade(PipelineSpec spec, EnvironmentSpec environment, string release)
    {
        var values = string.IsNullOrWhiteSpace(environment.ParametersFile) ? "" : $" -f {environment.ParametersFile}";
        return $"helm upgrade --install {release} {spec.Deploy.HelmChart} --namespace {spec.Deploy.Namespace} --create-namespace{values} --set image.repository=\"${{image%:*}}\" --set image.tag=\"${{image##*:}}\" --wait --timeout 5m";
    }

    /// <summary>The lines that update the cluster; image is the image as the shell reads it (quoted when it has variables).</summary>
    public static string Rollout(PipelineSpec spec, EnvironmentSpec environment, string image)
    {
        var deploy = spec.Deploy;
        if (spec.UsesHelm && deploy.Strategy != DeployStrategy.Standard)
        {
            return Strategies.Helm(spec, environment, image);
        }

        if (spec.UsesHelm)
        {
            // The chart reads image.repository and image.tag, as the charts helm create writes do.
            var values = string.IsNullOrWhiteSpace(environment.ParametersFile) ? "" : $" -f {environment.ParametersFile}";
            return string.Join('\n',
            [
                InstallHelm,
                $"image={image}",
                $"helm upgrade --install {deploy.DeploymentName} {deploy.HelmChart} --namespace {deploy.Namespace} --create-namespace{values} --set image.repository=\"${{image%:*}}\" --set image.tag=\"${{image##*:}}\" --wait --timeout 5m",
            ]);
        }

        if (deploy.Strategy != DeployStrategy.Standard)
        {
            return Strategies.Kubernetes(spec, environment, image);
        }

        return string.Join('\n',
        [
            $"kubectl apply -n {deploy.Namespace} -f {deploy.Manifests}",
            $"kubectl set image -n {deploy.Namespace} deployment/{deploy.DeploymentName} {deploy.ContainerName}={image}",
            $"kubectl rollout status -n {deploy.Namespace} deployment/{deploy.DeploymentName} --timeout=300s",
        ]);
    }
}
