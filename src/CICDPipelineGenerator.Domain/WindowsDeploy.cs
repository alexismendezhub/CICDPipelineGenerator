using System.Globalization;
using System.Text;

namespace CICDPipelineGenerator.Domain;

/// <summary>
/// The Windows PowerShell 5.1 scripts that deploy to a Windows service or an IIS site on the environment's own server.
/// Every system runs the same script through the runner or agent installed on the server: stop, back up the current
/// folder, mirror the new files into it (leaving the files to keep), start, check that it runs and answers, and on any
/// failure put the previous version back and start it again. The scripts use no $( ) so Azure DevOps never reads
/// them as its own macros.
/// </summary>
public static class WindowsDeploy
{
    /// <summary>The folder with the built files on each system, as a PowerShell expression.</summary>
    public static string Source(Platform platform) => platform switch
    {
        Platform.GitHubActions => "Join-Path $env:GITHUB_WORKSPACE 'out\\app'",
        Platform.AzureDevOps => "Join-Path $env:PIPELINE_WORKSPACE 'app'",
        Platform.GitLab => "Join-Path $env:CI_PROJECT_DIR 'out\\app'",
        _ => "Join-Path (Get-Location) 'out\\app'",
    };

    public static string Script(PipelineSpec spec, EnvironmentSpec environment, Platform platform) =>
        spec.Deploy.Target switch
        {
            Target.Iis => Iis(spec, environment, platform),
            Target.ScheduledTask => Task(spec, environment, platform),
            _ => Service(spec, environment, platform),
        };

    /// <summary>The same script with another ending: stop, put the previous version back, start and check.</summary>
    public static string RollbackScript(PipelineSpec spec, EnvironmentSpec environment, Platform platform) =>
        Script(spec, environment, platform).Replace(Deploy(), Rollback(), StringComparison.Ordinal);

    private static string Rollback() => """
        $backup = $target + '.previous'
        if (-not (Test-Path -LiteralPath $backup)) { throw ('There is no previous version in ' + $backup) }
        Stop-App
        Write-Host ('Putting the previous version back from ' + $backup)
        Copy-Folder $backup $target
        Start-App
        Test-Health
        Write-Host 'The previous version is running.'
        $global:LASTEXITCODE = 0
        """.Replace("\r\n", "\n") + "\n";

    /// <summary>A scheduled task is disabled and stopped while its folder changes, then enabled again; it runs at its next trigger.</summary>
    private static string Task(PipelineSpec spec, EnvironmentSpec environment, Platform platform)
    {
        var text = Common(spec, environment, platform);
        text.AppendLine(CultureInfo.InvariantCulture, $"$name = {Quote(environment.ResourceName!)}");
        text.Append("""
            $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            if ($null -eq $task) { throw ('There is no scheduled task named ' + $name + ' on ' + $env:COMPUTERNAME + '.') }

            """);
        text.AppendLine(string.IsNullOrWhiteSpace(environment.TargetPath)
            ? "$target = Split-Path -Parent ($task.Actions[0].Execute -replace '\"', '')"
            : $"$target = {Quote(environment.TargetPath)}");
        text.Append("""

            function Stop-App {
                Write-Host ('Disabling and stopping the task ' + $name + ' on ' + $env:COMPUTERNAME)
                Disable-ScheduledTask -TaskName $name | Out-Null
                Stop-ScheduledTask -TaskName $name
                $deadline = (Get-Date).Add($timeout)
                while ((Get-ScheduledTask -TaskName $name).State -eq 'Running') {
                    if ((Get-Date) -gt $deadline) { throw ('The task ' + $name + ' did not stop.') }
                    Start-Sleep -Seconds 1
                }
            }

            function Start-App {
                Write-Host ('Enabling the task ' + $name + '; it runs at its next trigger')
                Enable-ScheduledTask -TaskName $name | Out-Null
            }


            """);
        text.Append(Deploy());
        return text.ToString().Replace("\r\n", "\n");
    }


    private static string Service(PipelineSpec spec, EnvironmentSpec environment, Platform platform)
    {
        var deploy = spec.Deploy;
        var text = Common(spec, environment, platform);
        text.AppendLine(CultureInfo.InvariantCulture, $"$name = {Quote(environment.ResourceName!)}");
        text.Append("""
            $service = Get-CimInstance -ClassName Win32_Service -Filter ("Name='" + $name + "'")
            $create = $false
            if ($null -eq $service) {

            """);
        if (string.IsNullOrWhiteSpace(deploy.ServiceExecutable))
        {
            text.AppendLine("    throw ('There is no Windows service named ' + $name + ' on ' + $env:COMPUTERNAME + '.')");
        }
        else
        {
            text.AppendLine("    Write-Host ('The service ' + $name + ' does not exist yet: it is created after the copy.')");
            text.AppendLine("    $create = $true");
        }

        text.AppendLine("}");
        if (string.IsNullOrWhiteSpace(environment.TargetPath))
        {
            text.Append("""
                elseif ($service.PathName -match '^"([^"]+)"') {
                    $target = Split-Path -Parent $Matches[1]
                }
                else {
                    $target = Split-Path -Parent ($service.PathName -split ' ')[0]
                }

                """);
        }
        else
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"$target = {Quote(environment.TargetPath)}");
        }

        text.Append("""

            function Stop-App {
                $current = Get-CimInstance -ClassName Win32_Service -Filter ("Name='" + $name + "'")
                if ($null -eq $current -or $current.State -eq 'Stopped') { return }
                Write-Host ('Stopping ' + $name + ' on ' + $env:COMPUTERNAME)
                Stop-Service -Name $name -Force
                (Get-Service -Name $name).WaitForStatus('Stopped', $timeout)
                # The files stay locked until the process has really gone.
                if ($current.ProcessId -gt 0) {
                    Wait-Process -Id $current.ProcessId -Timeout $timeout.TotalSeconds -ErrorAction SilentlyContinue
                    if (Get-Process -Id $current.ProcessId -ErrorAction SilentlyContinue) { throw ('The process of ' + $name + ' did not end.') }
                }
            }

            function Start-App {

            """);
        if (!string.IsNullOrWhiteSpace(deploy.ServiceExecutable))
        {
            text.AppendLine("    if ($create) {");
            text.AppendLine(CultureInfo.InvariantCulture, $"        $program = Join-Path $target {Quote(deploy.ServiceExecutable)}");
            text.AppendLine("        New-Service -Name $name -BinaryPathName ('\"' + $program + '\"') -StartupType Automatic | Out-Null");
            text.AppendLine("        $script:create = $false");
            text.AppendLine("    }");
        }

        text.Append("""
                Write-Host ('Starting ' + $name)
                Start-Service -Name $name
                (Get-Service -Name $name).WaitForStatus('Running', $timeout)
                # A service that fails during start-up stops again within seconds.
                Start-Sleep -Seconds 5
                if ((Get-Service -Name $name).Status -ne 'Running') { throw ($name + ' stopped right after starting.') }
            }


            """);
        text.Append(Deploy());
        return text.ToString().Replace("\r\n", "\n");
    }

    private static string Iis(PipelineSpec spec, EnvironmentSpec environment, Platform platform)
    {
        var text = Common(spec, environment, platform);
        text.AppendLine("Import-Module WebAdministration");
        text.AppendLine(CultureInfo.InvariantCulture, $"$siteName = {Quote(environment.ResourceName!)}");
        text.Append("""
            $site = Get-Item -LiteralPath ('IIS:\Sites\' + $siteName) -ErrorAction SilentlyContinue
            if ($null -eq $site) { throw ('There is no IIS site named ' + $siteName + ' on ' + $env:COMPUTERNAME + '.') }

            """);
        text.AppendLine(string.IsNullOrWhiteSpace(environment.AppPool)
            ? "$pool = $site.applicationPool"
            : $"$pool = {Quote(environment.AppPool)}");
        text.AppendLine(string.IsNullOrWhiteSpace(environment.TargetPath)
            ? "$target = [Environment]::ExpandEnvironmentVariables($site.physicalPath)"
            : $"$target = {Quote(environment.TargetPath)}");
        text.Append("""
            $create = $false

            function Wait-Pool([string] $state) {
                $deadline = (Get-Date).Add($timeout)
                while ((Get-WebAppPoolState -Name $pool).Value -ne $state) {
                    if ((Get-Date) -gt $deadline) { throw ('The application pool ' + $pool + ' did not reach ' + $state + '.') }
                    Start-Sleep -Seconds 1
                }
            }

            function Stop-App {
                if ((Get-WebAppPoolState -Name $pool).Value -eq 'Stopped') { return }
                Write-Host ('Stopping the application pool ' + $pool + ' on ' + $env:COMPUTERNAME)
                Stop-WebAppPool -Name $pool
                Wait-Pool 'Stopped'
            }

            function Start-App {
                Write-Host ('Starting the application pool ' + $pool)
                Start-WebAppPool -Name $pool
                Wait-Pool 'Started'
            }

            """);
        text.Append(Deploy());
        return text.ToString().Replace("\r\n", "\n");
    }

    /// <summary>
    /// Writes the environment's settings into the settings file of the build, before it is copied: a JSON file
    /// (appsettings.json, levels separated by :) or an XML .config file (appSettings keys, ConnectionStrings:Name).
    /// The values arrive as the environment variables CFG_1, CFG_2...
    /// </summary>
    public static string SettingsScript(PipelineSpec spec, EnvironmentSpec environment, Platform platform)
    {
        var text = new StringBuilder();
        text.AppendLine("$ErrorActionPreference = 'Stop'");
        text.AppendLine(CultureInfo.InvariantCulture, $"$file = Join-Path ({Source(platform)}) {Quote(spec.Deploy.SettingsFile.Replace('/', '\\'))}");
        text.AppendLine("if (-not (Test-Path -LiteralPath $file)) { throw ('The settings file is missing: ' + $file) }");
        text.AppendLine("$settings = [ordered]@{}");
        for (var index = 0; index < environment.Settings.Count; index++)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"$settings[{Quote(environment.Settings[index].Name)}] = $env:{DeployExtras.Variable(index)}");
        }

        text.Append("""
            if ($file -like '*.config') {
                $xml = New-Object System.Xml.XmlDocument
                $xml.PreserveWhitespace = $true
                $xml.Load($file)
                foreach ($key in $settings.Keys) {
                    if ($key -like 'ConnectionStrings:*') {
                        $node = $xml.SelectSingleNode("/configuration/connectionStrings/add[@name='" + $key.Substring(18) + "']")
                        $attribute = 'connectionString'
                    }
                    else {
                        $node = $xml.SelectSingleNode("/configuration/appSettings/add[@key='" + $key + "']")
                        $attribute = 'value'
                    }
                    if ($null -eq $node) { throw ('The setting ' + $key + ' is not in ' + $file) }
                    $node.SetAttribute($attribute, [string] $settings[$key])
                }
                $xml.Save($file)
            }
            else {
                $json = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
                foreach ($key in $settings.Keys) {
                    $parts = $key -split ':'
                    $node = $json
                    for ($i = 0; $i -lt $parts.Count - 1; $i++) {
                        if ($null -eq $node.PSObject.Properties[$parts[$i]]) {
                            $node | Add-Member -NotePropertyName $parts[$i] -NotePropertyValue (New-Object PSObject)
                        }
                        $node = $node.PSObject.Properties[$parts[$i]].Value
                    }
                    $leaf = $parts[$parts.Count - 1]
                    if ($null -eq $node.PSObject.Properties[$leaf]) {
                        $node | Add-Member -NotePropertyName $leaf -NotePropertyValue ([string] $settings[$key])
                    }
                    else {
                        $node.PSObject.Properties[$leaf].Value = [string] $settings[$key]
                    }
                }
                [IO.File]::WriteAllText($file, ($json | ConvertTo-Json -Depth 32))
            }
            Write-Host ('Wrote ' + $settings.Count + ' settings into ' + $file)

            """);
        return text.ToString().Replace("\r\n", "\n");
    }

    /// <summary>The shared part with other stop and start functions (and the target folder) in the middle, for the tests.</summary>
    internal static string Harness(PipelineSpec spec, EnvironmentSpec environment, Platform platform, string middle) =>
        Common(spec, environment, platform).Append(middle).Append(Deploy()).ToString().Replace("\r\n", "\n");

    /// <summary>The settings and helpers both scripts share: where the files come from, what to keep, how to copy and check.</summary>
    private static StringBuilder Common(PipelineSpec spec, EnvironmentSpec environment, Platform platform)
    {
        var deploy = spec.Deploy;
        var text = new StringBuilder();
        text.AppendLine("$ErrorActionPreference = 'Stop'");
        text.AppendLine(CultureInfo.InvariantCulture, $"$source = {Source(platform)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"$timeout = [TimeSpan]::FromSeconds({deploy.StopTimeoutSeconds})");
        text.AppendLine(CultureInfo.InvariantCulture, $"$keep = @({string.Join(", ", deploy.KeepOnServer.Select(Quote))})");
        text.AppendLine(CultureInfo.InvariantCulture, $"$healthUrl = {Quote(environment.HealthCheckUrl ?? "")}");
        text.Append("""

            # Mirrors one folder into another; the files and folders to keep are neither copied nor deleted.
            function Copy-Folder([string] $from, [string] $to) {
                $arguments = @($from, $to, '/MIR', '/R:3', '/W:5', '/NFL', '/NDL', '/NP')
                if ($keep.Count -gt 0) { $arguments += @('/XF') + $keep + @('/XD') + $keep }
                & robocopy @arguments | Out-Host
                # robocopy answers 0 to 7 when it worked, 8 or more when it failed.
                if ($LASTEXITCODE -ge 8) { throw ('robocopy failed (' + $LASTEXITCODE + ') copying ' + $from + ' to ' + $to) }
                $global:LASTEXITCODE = 0
            }

            function Test-Health {
                if (-not $healthUrl) { return }
                $deadline = (Get-Date).Add($timeout)
                while ($true) {
                    try {
                        $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 10
                        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 400) { Write-Host ($healthUrl + ' answered ' + $response.StatusCode); return }
                    }
                    catch { Write-Host ($healthUrl + ' does not answer yet: ' + $_.Exception.Message) }
                    if ((Get-Date) -gt $deadline) { throw ($healthUrl + ' did not answer in time.') }
                    Start-Sleep -Seconds 5
                }
            }


            """);
        return text;
    }

    /// <summary>Stop, back up, copy, start, check; on failure put the previous version back.</summary>
    private static string Deploy() => """
        if (-not (Test-Path -LiteralPath $source)) { throw ('The built files are missing: ' + $source) }
        $backup = $target + '.previous'
        $hasBackup = Test-Path -LiteralPath $target
        Stop-App
        if ($hasBackup) {
            Write-Host ('Keeping the current version in ' + $backup)
            Copy-Folder $target $backup
        }
        else {
            New-Item -ItemType Directory -Path $target | Out-Null
        }

        try {
            Write-Host ('Copying the new version to ' + $target)
            Copy-Folder $source $target
            Start-App
            Test-Health
            Write-Host 'Deployed.'
        }
        catch {
            $failure = $_
            Write-Warning ('The deployment failed: ' + $failure.Exception.Message)
            if ($hasBackup) {
                Write-Warning 'Putting the previous version back.'
                try { Stop-App } catch { Write-Warning $_.Exception.Message }
                Copy-Folder $backup $target
                Start-App
                Write-Warning 'The previous version is running again.'
            }
            throw $failure
        }

        $global:LASTEXITCODE = 0
        """.Replace("\r\n", "\n") + "\n";

    /// <summary>A PowerShell single-quoted string; the checks already refuse quotes, this doubles any left anyway.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
