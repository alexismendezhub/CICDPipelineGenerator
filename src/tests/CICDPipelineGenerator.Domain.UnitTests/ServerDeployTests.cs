using System.Diagnostics;
using CICDPipelineGenerator.Domain;
using YamlDotNet.RepresentationModel;

namespace CICDPipelineGenerator.Domain.UnitTests;

public class ServerDeployTests
{
    private static PipelineSpec Service()
    {
        var spec = Examples.Get("windows-service");
        spec.Platforms = [Platform.GitHubActions, Platform.AzureDevOps, Platform.GitLab];
        return spec;
    }

    private static PipelineSpec Site()
    {
        var spec = Examples.Get("iis-site");
        spec.Platforms = [Platform.GitHubActions, Platform.AzureDevOps, Platform.GitLab];
        return spec;
    }

    [Fact]
    public void GitHub_deploys_on_the_environments_self_hosted_windows_runners()
    {
        var root = YamlWriterTests.Parse(GitHubGenerator.Generate(Service()).Content);
        var prod = Map(Map(root, "jobs"), "deploy-prod");

        Assert.Equal(["self-hosted", "windows", "app-prod"], Seq(prod, "runs-on"));
        var steps = Steps(prod);
        Assert.Equal("actions/download-artifact@v7", Text(steps[0], "uses"));
        Assert.Equal("powershell", Text(steps[1], "shell"));
        var script = Text(steps[1], "run")!;
        Assert.Contains("$source = Join-Path $env:GITHUB_WORKSPACE 'out\\app'", script, StringComparison.Ordinal);
        Assert.Contains("$name = 'Billing.Worker'", script, StringComparison.Ordinal);
        Assert.Contains("$keep = @('appsettings.Production.json', 'logs')", script, StringComparison.Ordinal);
        Assert.Contains("Stop-Service -Name $name -Force", script, StringComparison.Ordinal);
        Assert.Contains("Wait-Process", script, StringComparison.Ordinal);
        Assert.Contains("Copy-Folder $backup $target", script, StringComparison.Ordinal);
        Assert.DoesNotContain("azure/login", GitHubGenerator.Generate(Service()).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Azure_DevOps_deploys_on_the_tagged_virtual_machines_of_the_environment()
    {
        var root = YamlWriterTests.Parse(AzureDevOpsGenerator.Generate(Service()).Content);
        var stage = ((YamlSequenceNode)root.Children[new YamlScalarNode("stages")]).Children.Cast<YamlMappingNode>().Single(item => Text(item, "stage") == "Deploy_prod");
        var job = (YamlMappingNode)((YamlSequenceNode)stage.Children[new YamlScalarNode("jobs")]).Children[0];
        var environment = Map(job, "environment");
        Assert.Equal("prod", Text(environment, "name"));
        Assert.Equal("VirtualMachine", Text(environment, "resourceType"));
        Assert.Equal("app-prod", Text(environment, "tags"));

        var rolling = Map(Map(job, "strategy"), "rolling");
        Assert.Equal("1", Text(rolling, "maxParallel"));
        var steps = Steps(Map(rolling, "deploy"));
        Assert.Equal("stop", Text(steps[1], "errorActionPreference"));
        Assert.Contains("$source = Join-Path $env:PIPELINE_WORKSPACE 'app'", Text(steps[1], "powershell"), StringComparison.Ordinal);

        // Azure DevOps would read $( ) as its own macro, so the scripts never use it.
        Assert.DoesNotContain("$(", WindowsDeploy.Script(Service(), Service().Environments[0], Platform.AzureDevOps), StringComparison.Ordinal);
        Assert.DoesNotContain("$(", WindowsDeploy.Script(Site(), Site().Environments[0], Platform.AzureDevOps), StringComparison.Ordinal);
        Assert.DoesNotContain("AzureCLI@2", AzureDevOpsGenerator.Generate(Service()).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void GitLab_deploys_on_the_runners_with_the_environments_tag()
    {
        var root = YamlWriterTests.Parse(GitLabGenerator.Generate(Service()).Content);
        var prod = Map(root, "deploy-prod");
        Assert.Equal(["app-prod"], Seq(prod, "tags"));
        Assert.Equal(["build", "deploy-test"], Seq(prod, "needs"));
        Assert.False(prod.Children.ContainsKey(new YamlScalarNode("image")));
        Assert.False(prod.Children.ContainsKey(new YamlScalarNode("extends")));
        Assert.Contains("$source = Join-Path $env:CI_PROJECT_DIR 'out\\app'", Seq(prod, "script")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Iis_stops_the_sites_pool_and_checks_the_health_address()
    {
        var site = Site();
        var script = WindowsDeploy.Script(site, site.Environments[2], Platform.GitHubActions);
        Assert.Contains("Import-Module WebAdministration", script, StringComparison.Ordinal);
        Assert.Contains("$siteName = 'portal-prod'", script, StringComparison.Ordinal);
        Assert.Contains("$pool = $site.applicationPool", script, StringComparison.Ordinal);
        Assert.Contains("$target = [Environment]::ExpandEnvironmentVariables($site.physicalPath)", script, StringComparison.Ordinal);
        Assert.Contains("Stop-WebAppPool -Name $pool", script, StringComparison.Ordinal);
        Assert.Contains("$healthUrl = 'https://portal-prod.contoso.com/health'", script, StringComparison.Ordinal);

        site.Environments[2].AppPool = "PortalPool";
        site.Environments[2].TargetPath = @"D:\Sites\Portal";
        script = WindowsDeploy.Script(site, site.Environments[2], Platform.GitHubActions);
        Assert.Contains("$pool = 'PortalPool'", script, StringComparison.Ordinal);
        Assert.Contains("$target = 'D:\\Sites\\Portal'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_service_is_created_only_when_its_program_is_given()
    {
        var spec = Service();
        var environment = spec.Environments[0];
        Assert.Contains("throw ('There is no Windows service named '", WindowsDeploy.Script(spec, environment, Platform.GitHubActions), StringComparison.Ordinal);

        spec.Deploy.ServiceExecutable = "Billing.Worker.exe";
        environment.TargetPath = @"D:\Services\Billing";
        var script = WindowsDeploy.Script(spec, environment, Platform.GitHubActions);
        Assert.Contains("New-Service -Name $name", script, StringComparison.Ordinal);
        Assert.Contains("$program = Join-Path $target 'Billing.Worker.exe'", script, StringComparison.Ordinal);

        // Creating it needs the folder.
        environment.TargetPath = null;
        Assert.Contains(PipelineRules.Check(spec), message => message.Text.Contains("to create the service", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Billing'; Remove-Item C:\\ -Recurse #")]
    [InlineData("Billing\"x")]
    public void A_service_name_that_could_break_the_script_is_refused(string name)
    {
        var spec = Service();
        spec.Environments[0].ResourceName = name;
        Assert.Contains(PipelineRules.Check(spec), message => message.Severity == Severity.Error && message.Text.Contains("Windows service name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"D:\Apps\Billing", true)]
    [InlineData(@"D:\Apps\Billing\", false)]
    [InlineData(@"Apps\Billing", false)]
    [InlineData(@"D:\Apps\Bill'ing", false)]
    [InlineData(@"\\server\share", false)]
    public void The_folder_is_a_full_windows_path(string path, bool valid)
    {
        var spec = Service();
        spec.Environments[0].TargetPath = path;
        Assert.Equal(!valid, PipelineRules.Check(spec).Any(message => message.Text.Contains("full Windows path", StringComparison.Ordinal)));
    }

    [Fact]
    public void Servers_need_a_label_and_no_azure_identity()
    {
        var spec = Service();
        spec.Environments[1].ServerLabel = null;
        spec.Environments[1].ServiceConnection = null;
        var messages = PipelineRules.Check(spec);
        Assert.Contains(messages, message => message.Text.StartsWith("Environment test: write the label", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Text.Contains("service connection", StringComparison.Ordinal));

        var guide = SetupGuide.Generate(Service()).Content;
        Assert.Contains("## 1. Servers", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("az identity create", guide, StringComparison.Ordinal);
        Assert.Contains("`app-prod`", guide, StringComparison.Ordinal);
        Assert.Contains("never touches `appsettings.Production.json`, `logs`", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_generated_script_parses_in_windows_powershell()
    {
        var powershell = PowerShell();
        if (powershell is null)
        {
            return;
        }

        var scripts = new List<string>();
        foreach (var spec in new[] { Service(), Site() })
        {
            foreach (var platform in GenerationTests.Six)
            {
                scripts.Add(WindowsDeploy.Script(spec, spec.Environments[0], platform));
            }
        }

        var created = Service();
        created.Deploy.ServiceExecutable = "Billing.Worker.exe";
        created.Environments[0].TargetPath = @"D:\Services\Billing";
        scripts.Add(WindowsDeploy.Script(created, created.Environments[0], Platform.GitHubActions));

        var folder = Directory.CreateTempSubdirectory("cicdpipelinegenerator-");
        try
        {
            for (var index = 0; index < scripts.Count; index++)
            {
                var path = Path.Combine(folder.FullName, $"deploy{index}.ps1");
                File.WriteAllText(path, scripts[index]);
                var command = "$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('" + path + "', [ref]$null, [ref]$errors); " +
                    "if ($errors) { $errors | ForEach-Object { $_.Message + ' line ' + $_.Extent.StartLineNumber }; exit 1 }";
                var (code, output) = Run(powershell, ["-NoProfile", "-NonInteractive", "-Command", command], null);
                Assert.True(code == 0, $"deploy{index}.ps1: {output}");
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Runs the real copy, keep and roll back part of the script in Windows PowerShell on temporary folders, with stop and
    /// start functions that stand in for the service (no service or IIS is touched).
    /// </summary>
    [Fact]
    public void The_copy_keeps_the_listed_files_and_a_failed_start_puts_the_previous_version_back()
    {
        var powershell = PowerShell();
        if (powershell is null)
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("cicdpipelinegenerator-");
        try
        {
            var target = Path.Combine(root.FullName, "app");
            var source = Path.Combine(root.FullName, "out", "app");
            Write(target, "Old.dll", "old");
            Write(target, "only-on-server.txt", "x");
            Write(target, "appsettings.Production.json", "server settings");
            Write(Path.Combine(target, "logs"), "today.log", "log");
            Write(source, "New.dll", "new");
            Write(source, "appsettings.Production.json", "build settings");

            var spec = Service();
            spec.Environments[0].HealthCheckUrl = null;

            // First run: the start works.
            var (code, output) = Run(powershell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script(root, spec, target, failStart: false)], root.FullName);
            Assert.True(code == 0, output);
            Assert.True(File.Exists(Path.Combine(target, "New.dll")));
            Assert.False(File.Exists(Path.Combine(target, "Old.dll")));
            Assert.False(File.Exists(Path.Combine(target, "only-on-server.txt")));
            Assert.Equal("server settings", File.ReadAllText(Path.Combine(target, "appsettings.Production.json")));
            Assert.True(File.Exists(Path.Combine(target, "logs", "today.log")));
            Assert.True(File.Exists(Path.Combine(target + ".previous", "Old.dll")));

            // Second run with a newer build whose start fails: the version from the first run comes back.
            Write(source, "Newer.dll", "newer");
            (code, output) = Run(powershell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script(root, spec, target, failStart: true)], root.FullName);
            Assert.NotEqual(0, code);
            Assert.Contains("Putting the previous version back", output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(target, "Newer.dll")));
            Assert.True(File.Exists(Path.Combine(target, "New.dll")));
            Assert.Equal("server settings", File.ReadAllText(Path.Combine(target, "appsettings.Production.json")));
            // Started once in the first run, then the failed start and the start of the previous version.
            Assert.Equal(3, File.ReadAllLines(Path.Combine(root.FullName, "starts.txt")).Length);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string Script(DirectoryInfo root, PipelineSpec spec, string target, bool failStart)
    {
        var starts = Path.Combine(root.FullName, "starts.txt");
        var middle = $$"""
            $target = '{{target}}'
            function Stop-App { Write-Host 'stopped' }
            function Start-App {
                Add-Content -LiteralPath '{{starts}}' -Value 'start'
                $count = (Get-Content -LiteralPath '{{starts}}').Count
                if ({{(failStart ? "$true" : "$false")}} -and $count -eq 2) { throw 'The service did not start.' }
            }

            """;
        var path = Path.Combine(root.FullName, failStart ? "fail.ps1" : "ok.ps1");
        File.WriteAllText(path, WindowsDeploy.Harness(spec, spec.Environments[0], Platform.GitHubActions, middle));
        return path;
    }

    private static void Write(string folder, string name, string content)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), content);
    }

    /// <summary>Windows PowerShell 5.1, what the servers run; the tests that need it check nothing elsewhere.</summary>
    internal static string? PowerShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Fails when Windows PowerShell finds a syntax error in any of the scripts (checks nothing off Windows).</summary>
    internal static void AssertParses(IEnumerable<string> scripts)
    {
        var powershell = PowerShell();
        if (powershell is null)
        {
            return;
        }

        var folder = Directory.CreateTempSubdirectory("cicdpipelinegenerator-");
        try
        {
            var index = 0;
            foreach (var script in scripts)
            {
                var path = Path.Combine(folder.FullName, $"script{index++}.ps1");
                File.WriteAllText(path, script);
                var command = "$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('" + path + "', [ref]$null, [ref]$errors); " +
                    "if ($errors) { $errors | ForEach-Object { $_.Message + ' line ' + $_.Extent.StartLineNumber }; exit 1 }";
                var (code, output) = Run(powershell, ["-NoProfile", "-NonInteractive", "-Command", command], null);
                Assert.True(code == 0, $"{output}\n{script}");
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    internal static (int Code, string Output) Run(string program, string[] arguments, string? workspace, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (workspace is not null)
        {
            start.Environment["GITHUB_WORKSPACE"] = workspace;
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error);
    }

    private static List<YamlMappingNode> Steps(YamlMappingNode job) =>
        ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToList();

    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node.Children[new YamlScalarNode(key)];

    private static string? Text(YamlMappingNode node, string key) => ((YamlScalarNode)node.Children[new YamlScalarNode(key)]).Value;

    private static List<string> Seq(YamlMappingNode node, string key) =>
        ((YamlSequenceNode)node.Children[new YamlScalarNode(key)]).Children.Select(item => ((YamlScalarNode)item).Value!).ToList();
}
