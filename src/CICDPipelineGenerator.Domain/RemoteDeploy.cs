using System.Globalization;

namespace CICDPipelineGenerator.Domain;

/// <summary>
/// Deploys to servers that have no runner: a hosted agent copies the build to each host of the environment and runs
/// the same deploy script there, over SSH (Linux, with the key and known hosts kept as secrets) or over WinRM with
/// HTTPS (Windows, with a user and password kept as secrets). The hosts are updated one after another.
/// </summary>
public static class RemoteDeploy
{
    public const string SshKey = "DEPLOY_SSH_KEY";
    public const string SshKnownHosts = "DEPLOY_SSH_KNOWN_HOSTS";
    public const string SshUser = "DEPLOY_SSH_USER";
    public const string User = "DEPLOY_USER";
    public const string Password = "DEPLOY_PASSWORD";

    /// <summary>The secrets the remote deploy reads.</summary>
    public static IReadOnlyList<string> SecretNames(PipelineSpec spec) =>
        !spec.IsRemote ? [] : spec.UsesPowerShell ? [User, Password] : [SshUser, SshKey, SshKnownHosts];

    public static IReadOnlyList<KeyValuePair<string, string>> Env(PipelineSpec spec, EnvironmentSpec environment, Platform platform) =>
        SecretNames(spec).Select(name => new KeyValuePair<string, string>(name, Secrets.Reference(platform, environment, name))).ToList();

    private static string Folder(PipelineSpec spec) => "cicd-" + spec.Name;

    public static string Script(PipelineSpec spec, EnvironmentSpec environment, Platform platform, bool rollback) =>
        spec.UsesPowerShell ? WinRm(spec, environment, platform, rollback) : Ssh(spec, environment, platform, rollback);

    private static string Ssh(PipelineSpec spec, EnvironmentSpec environment, Platform platform, bool rollback)
    {
        var remote = $"\"$HOME/.{Folder(spec)}\"";
        var script = rollback ? LinuxDeploy.RollbackScript(spec, environment, remote) : LinuxDeploy.Script(spec, environment, remote);
        var lines = new List<string>
        {
            "set -euo pipefail",
            "mkdir -p ~/.ssh && chmod 700 ~/.ssh",
            "printf '%s\\n' \"$DEPLOY_SSH_KEY\" > ~/.ssh/deploy_key && chmod 600 ~/.ssh/deploy_key",
            // Only the hosts whose keys you stored are trusted.
            "printf '%s\\n' \"$DEPLOY_SSH_KNOWN_HOSTS\" > ~/.ssh/known_hosts",
            "ssh_options=\"-i $HOME/.ssh/deploy_key -o StrictHostKeyChecking=yes -o BatchMode=yes\"",
            "cat > /tmp/deploy.sh <<'DEPLOY'",
            script.TrimEnd('\n'),
            "DEPLOY",
            $"for server in {string.Join(" ", environment.Hosts)}; do",
            "  echo \"Deploying to $server\"",
        };
        if (!rollback)
        {
            lines.Add($"  rsync -az --delete -e \"ssh $ssh_options\" {LinuxDeploy.Source(platform)}/ \"$DEPLOY_SSH_USER@$server:.{Folder(spec)}/\"");
        }

        lines.Add("  ssh $ssh_options \"$DEPLOY_SSH_USER@$server\" 'bash -s' < /tmp/deploy.sh");
        if (!rollback && Strategies.ServerPause(spec, environment) is { } pause && Strategies.FirstServer(spec, environment) is { } first)
        {
            lines.Add($"  if [ \"$server\" = \"{first}\" ]; then {pause}; fi");
        }

        lines.Add("done");
        return string.Join('\n', lines) + "\n";
    }

    private static string WinRm(PipelineSpec spec, EnvironmentSpec environment, Platform platform, bool rollback)
    {
        var local = "$source = " + WindowsDeploy.Source(platform);
        var script = (rollback ? WindowsDeploy.RollbackScript(spec, environment, platform) : WindowsDeploy.Script(spec, environment, platform))
            .Replace(local, $"$source = Join-Path $env:TEMP {WindowsDeploy.Quote(Folder(spec))}", StringComparison.Ordinal);
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "$password = ConvertTo-SecureString $env:DEPLOY_PASSWORD -AsPlainText -Force",
            "$credential = New-Object System.Management.Automation.PSCredential ($env:DEPLOY_USER, $password)",
            "$deploy = @'",
            script.TrimEnd('\n'),
            "'@",
            string.Create(CultureInfo.InvariantCulture, $"foreach ($server in @({string.Join(", ", environment.Hosts.Select(WindowsDeploy.Quote))})) {{"),
            "    Write-Host ('Deploying to ' + $server)",
            "    $session = New-PSSession -ComputerName $server -UseSSL -Credential $credential",
            "    try {",
        };
        if (!rollback)
        {
            lines.Add($"        $remote = Invoke-Command -Session $session -ScriptBlock {{ Join-Path $env:TEMP {WindowsDeploy.Quote(Folder(spec))} }}");
            lines.Add("        Invoke-Command -Session $session -ArgumentList $remote -ScriptBlock { param($path) if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }");
            lines.Add($"        Copy-Item -Path ({WindowsDeploy.Source(platform)}) -Destination $remote -ToSession $session -Recurse -Force");
        }

        lines.Add("        Invoke-Command -Session $session -ScriptBlock ([scriptblock]::Create($deploy))");
        lines.Add("    }");
        lines.Add("    finally { Remove-PSSession $session }");
        if (!rollback && Strategies.ServerPause(spec, environment) is { } pause && Strategies.FirstServer(spec, environment) is { } first)
        {
            lines.Add($"    if ($server -eq {WindowsDeploy.Quote(first)}) {{ {pause} }}");
        }

        lines.Add("}");
        return string.Join('\n', lines) + "\n";
    }
}

/// <summary>The deploy step of an application on your own servers, for the shell of the job that runs it.</summary>
public static class ServerDeploy
{
    public static string Script(PipelineSpec spec, EnvironmentSpec environment, Platform platform, bool rollback = false)
    {
        if (spec.IsRemote)
        {
            return RemoteDeploy.Script(spec, environment, platform, rollback);
        }

        if (spec.Deploy.Target == Target.LinuxService)
        {
            var source = LinuxDeploy.Source(platform);
            return rollback ? LinuxDeploy.RollbackScript(spec, environment, source) : LinuxDeploy.Script(spec, environment, source);
        }

        return rollback ? WindowsDeploy.RollbackScript(spec, environment, platform) : WindowsDeploy.Script(spec, environment, platform);
    }
}
