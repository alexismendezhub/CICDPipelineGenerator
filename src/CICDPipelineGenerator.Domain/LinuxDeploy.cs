using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CICDPipelineGenerator.Domain;

/// <summary>
/// The bash script that deploys to a systemd service on the environment's own Linux server, the same way the Windows
/// script does: stop, keep the current version in &lt;folder&gt;.previous, mirror the build with rsync (leaving the
/// files to keep), start, check that it runs and answers, and put the previous version back when any of that fails.
/// systemctl runs through sudo without a password; the folder belongs to the account that deploys.
/// </summary>
public static class LinuxDeploy
{
    /// <summary>The folder with the built files in a deploy job, as bash writes it.</summary>
    public static string Source(Platform platform) => platform == Platform.AzureDevOps ? "\"$PIPELINE_WORKSPACE/app\"" : "\"$PWD/out/app\"";

    public static string Script(PipelineSpec spec, EnvironmentSpec environment, string source) => Head(spec, environment, source) + Deploy;

    public static string RollbackScript(PipelineSpec spec, EnvironmentSpec environment, string source) => Head(spec, environment, source) + Rollback;

    private static string Head(PipelineSpec spec, EnvironmentSpec environment, string source)
    {
        var deploy = spec.Deploy;
        var text = new StringBuilder();
        text.Append("set -euo pipefail\n");
        text.Append(CultureInfo.InvariantCulture, $"source_dir={source}\n");
        text.Append(CultureInfo.InvariantCulture, $"name={Quote(environment.ResourceName!)}\n");
        text.Append(CultureInfo.InvariantCulture, $"timeout={deploy.StopTimeoutSeconds}\n");
        text.Append(CultureInfo.InvariantCulture, $"health_url={Quote(environment.HealthCheckUrl ?? "")}\n");
        text.Append(CultureInfo.InvariantCulture, $"keep=({string.Join(" ", deploy.KeepOnServer.Select(name => "--exclude " + Quote(name)))})\n");
        text.Append("if ! systemctl cat \"$name\" > /dev/null 2>&1; then echo \"There is no systemd service named $name on $HOSTNAME.\" >&2; exit 1; fi\n");
        if (string.IsNullOrWhiteSpace(environment.TargetPath))
        {
            text.Append("target=`systemctl show -p WorkingDirectory --value \"$name\"`\n");
            text.Append("if [ -z \"$target\" ]; then echo \"The service $name has no WorkingDirectory: write its folder.\" >&2; exit 1; fi\n");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture, $"target={Quote(environment.TargetPath)}\n");
        }

        text.Append(Functions);
        return text.ToString();
    }

    private const string Functions = """
        backup="$target.previous"

        # Mirrors one folder into another; the files and folders to keep are neither copied nor deleted.
        copy_folder() { rsync -a --delete ${keep[@]+"${keep[@]}"} "$1/" "$2/"; }

        stop_app() {
          echo "Stopping $name on $HOSTNAME"
          sudo -n systemctl stop "$name"
        }

        start_app() {
          echo "Starting $name"
          sudo -n systemctl start "$name" || return 1
          # A service that fails during start-up stops again within seconds.
          sleep 5
          systemctl is-active --quiet "$name" || { echo "$name stopped right after starting." >&2; return 1; }
        }

        test_health() {
          [ -z "$health_url" ] && return 0
          declare -i deadline=SECONDS+timeout
          until curl -fsS -o /dev/null --max-time 10 "$health_url"; do
            if [ "$SECONDS" -ge "$deadline" ]; then echo "$health_url did not answer in time." >&2; return 1; fi
            sleep 5
          done
          echo "$health_url answered"
        }


        """;

    private const string Deploy = """
        [ -d "$source_dir" ] || { echo "The built files are missing: $source_dir" >&2; exit 1; }
        has_backup=false
        stop_app
        if [ -d "$target" ]; then
          has_backup=true
          echo "Keeping the current version in $backup"
          mkdir -p "$backup"
          copy_folder "$target" "$backup"
        else
          mkdir -p "$target"
        fi

        echo "Copying the new version to $target"
        if copy_folder "$source_dir" "$target" && start_app && test_health; then
          echo 'Deployed.'
        else
          echo 'The deployment failed.' >&2
          if [ "$has_backup" = true ]; then
            echo 'Putting the previous version back.' >&2
            stop_app || true
            copy_folder "$backup" "$target"
            start_app
            echo 'The previous version is running again.' >&2
          fi
          exit 1
        fi

        """;

    private const string Rollback = """
        [ -d "$backup" ] || { echo "There is no previous version in $backup" >&2; exit 1; }
        stop_app
        echo "Putting the previous version back from $backup"
        copy_folder "$backup" "$target"
        start_app
        test_health
        echo 'The previous version is running.'

        """;

    /// <summary>Writes the environment's settings (CFG_1, CFG_2...) into the JSON settings file of the build, with the Python every server and agent has.</summary>
    public static string SettingsScript(PipelineSpec spec, EnvironmentSpec environment, Platform platform)
    {
        var folder = platform == Platform.AzureDevOps ? "$PIPELINE_WORKSPACE/app" : Commands.PackageFolder;
        var names = JsonSerializer.Serialize(environment.Settings.Select(setting => setting.Name));
        return $"python3 - \"{folder}/{spec.Deploy.SettingsFile}\" <<'PY'\n" + $"names = {names}\n" + SettingsProgram + "PY";
    }

    private const string SettingsProgram = """
        import json, os, sys
        path = sys.argv[1]
        with open(path, encoding="utf-8-sig") as file:
            data = json.load(file)
        for index, name in enumerate(names):
            node = data
            parts = name.split(":")
            for part in parts[:-1]:
                node = node.setdefault(part, {})
            node[parts[-1]] = os.environ["CFG_%d" % (index + 1)]
        with open(path, "w", encoding="utf-8") as file:
            json.dump(data, file, indent=2)
        print("Wrote %d settings into %s" % (len(names), path))

        """;

    /// <summary>A bash single-quoted string; the checks already refuse quotes.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
