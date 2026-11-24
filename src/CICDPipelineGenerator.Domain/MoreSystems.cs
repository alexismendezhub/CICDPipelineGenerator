using System.Text;
using System.Text.RegularExpressions;

namespace CICDPipelineGenerator.Domain;

/// <summary>
/// Travis CI, Google Cloud Build, AWS CodeBuild (for CodePipeline), TeamCity and Argo Workflows. They run the same
/// jobs as the other shell-based systems, each job as one bash script in a Linux image; what a system has no word
/// for (approvals, schedules, who may deploy) is set in the system itself, as SETUP.md says.
/// </summary>
public static partial class MoreSystems
{
    public static bool Is(Platform platform) => platform is Platform.TravisCi or Platform.CloudBuild or Platform.CodePipeline or Platform.TeamCity or Platform.ArgoWorkflows;

    /// <summary>A whole job as one script: the sign-in, then every step under its name.</summary>
    public static string Script(PipelineSpec spec, PlanJob job, Platform platform, string? first = null)
    {
        var lines = new List<string> { "set -e" };
        if (spec.NotifyOnFailure)
        {
            // Whatever stops the job, the team is told.
            lines.Add($"tell_failure() {{\n  if [ \"$?\" != 0 ]; then\n    export WEBHOOK=\"$NOTIFY_WEBHOOK\" TEXT=\"The pipeline of {spec.Name} failed{Where(platform)}\"\n    {Commands.Notify} || true\n  fi\n}}\ntrap tell_failure EXIT");
        }

        if (!string.IsNullOrEmpty(first))
        {
            lines.Add(first);
        }

        if (job.Preamble.Length > 0)
        {
            lines.Add(job.Preamble);
        }

        foreach (var step in job.Steps)
        {
            lines.Add($"echo \"== {step.Name.Replace("\"", "'", StringComparison.Ordinal)}\"");
            lines.Add("cd \"$JOB_ROOT\"");
            lines.Add(Systems.WithEnv(step.Script, step.Env, powerShell: false));
        }

        if (job.NotifiesSuccess)
        {
            lines.Add($"export WEBHOOK=\"$NOTIFY_WEBHOOK\" TEXT=\"{spec.Name} is deployed to {job.Environment!.Name}{Where(platform)}\"\n{Commands.Notify}");
        }

        // Every step starts from the folder the job started in.
        lines.Insert(1, "JOB_ROOT=\"$PWD\"");
        return string.Join('\n', lines);
    }

    /// <summary>The shell an image has: the Docker client image is Alpine, with sh only.</summary>
    public static string Shell(PlanJob job) => job.Image.StartsWith("docker:", StringComparison.Ordinal) ? "sh" : "bash";

    /// <summary>The variable of the system's store a job reads: the environment's own for a deploy, the shared one for the build.</summary>
    public static string Scoped(PlanJob job, string name) => job.Environment is null || Shared(name) ? name : Secrets.AzureDevOpsName(job.Environment, name);

    /// <summary>The webhook is one for the whole pipeline, whatever the environment.</summary>
    private static bool Shared(string name) => name == JenkinsGenerator.NotifyCredential;

    private static string Where(Platform platform) => Systems.RunUrl(platform) is { Length: > 0 } url ? ": " + url : ".";

    /// <summary>Everything the job reads from the system's store.</summary>
    public static IReadOnlyList<string> Names(PipelineSpec spec, PlanJob job) =>
        spec.NotifyOnFailure || job.NotifiesSuccess ? [.. job.Variables, JenkinsGenerator.NotifyCredential] : job.Variables;

    /// <summary>The jobs that run on a push, and after them the ones started by hand that put the previous version back.</summary>
    private static string RollbackGuard(PlanJob job, string value) =>
        $"if [ \"{value}\" != \"{job.Environment!.Name}\" ]; then echo \"Not the environment asked for.\"; exit 0; fi";

    /// <summary>The line that stops a deploy job on a branch or run that does not deploy to its environment.</summary>
    private static string Guard(PipelineSpec spec, PlanJob job, string branch, string tag) =>
        !job.NotOnPullRequests ? ""
        : spec.Flow == Flow.Tag ? $"if [ -z \"{tag}\" ]; then echo \"Only a tag deploys.\"; exit 0; fi"
        : job.Environment is null ? ""
        : $"if [ \"{branch}\" != \"{spec.BranchOf(job.Environment)}\" ]; then echo \"{job.Environment.Name} deploys from {spec.BranchOf(job.Environment)}.\"; exit 0; fi";

    // ---------------------------------------------------------------- Travis CI

    public const string TravisPath = ".travis.yml";

    public static GeneratedFile Travis(PipelineSpec spec)
    {
        var plan = ShellPlan.Create(spec, Platform.TravisCi);
        var include = new YSeq();
        foreach (var job in plan.Jobs)
        {
            var branches = job.Environment is null ? spec.DeployBranches() : [spec.BranchOf(job.Environment)];
            var push = spec.Flow == Flow.Tag && job.NotOnPullRequests
                ? "tag IS present"
                : "type = push AND (" + string.Join(" OR ", branches.Select(branch => $"branch = {branch}")) + ")";
            // Travis has one list of variables for the repository, so an environment's carry its name.
            var passed = Names(spec, job).Select(name => Scoped(job, name) == name ? $" -e {name}" : $" -e {name}=\"${Scoped(job, name)}\"").Concat([" -e TRAVIS_COMMIT", " -e TRAVIS_BUILD_NUMBER", " -e TRAVIS_BUILD_WEB_URL"]);
            var script = new YSeq(
            [
                // The job's script runs inside the image that has its tools; the repository is mounted in it.
                $"cat > \"$HOME/job.sh\" <<'JOB_SCRIPT'\n{Script(spec, job, Platform.TravisCi)}\nJOB_SCRIPT",
                $"docker run --rm -v \"$PWD:/work\" -v \"$HOME/job.sh:/job.sh:ro\" -w /work{(job.Services.Count > 0 ? " --network host" : "")}{(job.Docker ? " -v /var/run/docker.sock:/var/run/docker.sock" : "")}{string.Concat(passed)} {job.Image} {Shell(job)} /job.sh",
            ]);
            if (job.Services.Count > 0)
            {
                // The services run on the machine of the job, which the job's container shares the network with.
                script.Insert(0, new YScalar(Services.Start(spec), Raw: false));
            }

            include.Add(new YMap
            {
                { "stage", job.Name },
                { "name", job.Name },
                { "if", job.NotOnPullRequests || !spec.BuildPullRequests ? push : $"({push}) OR type = pull_request" },
                { "workspaces", job.Keeps.Count > 0 ? new YMap { { "create", new YMap { { "name", "build" }, { "paths", new YSeq(job.Keeps) } } } } : job.UsesBuild ? new YMap { { "use", "build" } } : null },
                { "script", script },
            });
        }

        var root = new YMap
        {
            { "os", "linux" },
            { "dist", "jammy" },
            { "language", "minimal" },
            { "services", new YSeq(["docker"]) },
            { "jobs", new YMap { { "include", include } } },
        };
        return new(TravisPath, YamlWriter.Write(root, Header(spec)));
    }

    // ---------------------------------------------------------------- Google Cloud Build

    public const string CloudBuildPath = "cloudbuild.yaml";

    public static GeneratedFile CloudBuild(PipelineSpec spec)
    {
        var plan = ShellPlan.Create(spec, Platform.CloudBuild);
        var steps = new YSeq();
        var secrets = new List<string>();
        var started = new List<string>();
        foreach (var job in plan.Jobs)
        {
            foreach (var service in job.Services.Where(service => !started.Contains(service.Name)))
            {
                // Every step of a build is on the network cloudbuild, where the service answers by its name.
                started.Add(service.Name);
                steps.Add(new YMap
                {
                    { "id", "service-" + service.Name },
                    { "name", "gcr.io/cloud-builders/docker" },
                    { "args", new YSeq(["run", "-d", "--network", "cloudbuild", "--name", service.Name, .. service.Env.SelectMany(pair => new[] { "-e", $"{pair.Key}={pair.Value}" }), service.Image]) },
                });
            }

            // A secret has one name for the whole build, so an environment's secrets carry its name and the script gives them back their own.
            steps.Add(CloudBuildStep(spec, job, secrets, Guard(spec, job, "$BRANCH_NAME", "$TAG_NAME"), started));
        }

        var root = new YMap
        {
            { "steps", steps },
            // The build's own values (COMMIT_SHA, BRANCH_NAME, TAG_NAME, BUILD_ID, PROJECT_ID) reach the scripts as variables.
            { "options", new YMap { { "automapSubstitutions", true }, { "logging", "CLOUD_LOGGING_ONLY" } } },
        };
        if (secrets.Count > 0)
        {
            var list = new YSeq();
            foreach (var name in secrets)
            {
                list.Add(new YMap { { "versionName", $"projects/$PROJECT_ID/secrets/{name}/versions/latest" }, { "env", name } });
            }

            root.Add("availableSecrets", new YMap { { "secretManager", list } });
        }

        root.Add("timeout", "3600s");
        return new(CloudBuildPath, YamlWriter.Write(root, Header(spec)));
    }

    private static YMap CloudBuildStep(PipelineSpec spec, PlanJob job, List<string> secrets, string guard, List<string> started)
    {
        _ = started;
        var names = Names(spec, job);
        var mine = names.Select(name => Scoped(job, name)).ToList();
        secrets.AddRange(mine.Where(name => !secrets.Contains(name)));
        var first = string.Join('\n', new[] { guard }
            .Concat(names.Where(name => Scoped(job, name) != name).Select(name => $"export {name}=\"${Scoped(job, name)}\""))
            .Where(line => line.Length > 0));
        return new YMap
        {
            { "id", job.Id },
            { "name", job.Image },
            { "script", $"#!/usr/bin/env {Shell(job)}\n" + Script(spec, job, Platform.CloudBuild, first) },
            { "secretEnv", mine.Count > 0 ? new YSeq(mine) : null },
        };
    }

    public const string CloudBuildRollbackPath = "cloudbuild-rollback.yaml";

    /// <summary>A second build, started by hand with the substitution _ENVIRONMENT, that puts the previous version back.</summary>
    public static GeneratedFile CloudBuildRollback(PipelineSpec spec)
    {
        var plan = ShellPlan.Create(spec, Platform.CloudBuild);
        var steps = new YSeq();
        var secrets = new List<string>();
        foreach (var job in plan.Rollbacks)
        {
            steps.Add(CloudBuildStep(spec, job, secrets, RollbackGuard(job, "$_ENVIRONMENT"), []));
        }

        var root = new YMap
        {
            { "steps", steps },
            { "substitutions", new YMap { { "_ENVIRONMENT", spec.Environments[0].Name } } },
            { "options", new YMap { { "automapSubstitutions", true }, { "logging", "CLOUD_LOGGING_ONLY" } } },
        };
        if (secrets.Count > 0)
        {
            var list = new YSeq();
            foreach (var name in secrets)
            {
                list.Add(new YMap { { "versionName", $"projects/$PROJECT_ID/secrets/{name}/versions/latest" }, { "env", name } });
            }

            root.Add("availableSecrets", new YMap { { "secretManager", list } });
        }

        return new(CloudBuildRollbackPath, YamlWriter.Write(root, [$"Generated by CI/CD Pipeline Generator for {spec.Name}.", "Run it by hand with _ENVIRONMENT set to the environment that goes back to its previous version."]));
    }

    // ---------------------------------------------------------------- AWS CodeBuild, for CodePipeline

    public static string BuildspecPath(PlanJob job) => job.Id == "build" ? "buildspec.yml" : $"buildspec-{job.Id}.yml";

    /// <summary>The Parameter Store name of a variable: /application/environment/NAME, or /application/build/NAME.</summary>
    public static string Parameter(PipelineSpec spec, PlanJob job, string name) => $"/{spec.Name}/{(Shared(name) ? "build" : job.Environment?.Name ?? "build")}/{name}";

    public static IReadOnlyList<GeneratedFile> CodeBuild(PipelineSpec spec)
    {
        var plan = ShellPlan.Create(spec, Platform.CodePipeline);
        var files = new List<GeneratedFile>();
        foreach (var job in plan.Jobs.Concat(plan.Rollbacks))
        {
            var env = new YMap { { "shell", "bash" } };
            if (Names(spec, job).Count > 0)
            {
                var store = new YMap();
                foreach (var name in Names(spec, job))
                {
                    store.Add(name, Parameter(spec, job, name));
                }

                env.Add("parameter-store", store);
            }

            var root = new YMap
            {
                { "version", new YScalar("0.2", Raw: true) },
                { "env", env },
                { "phases", new YMap { { "build", new YMap { { "commands", new YSeq([Script(spec, job, Platform.CodePipeline)]) } } } } },
            };
            if (job.Cache is { } stack)
            {
                // Kept between builds when the project's cache is turned on (S3 or local).
                root.Add("cache", new YMap { { "paths", new YSeq([$"/root/{PackageCache.Folder(stack)}/**/*"]) } });
            }

            if (job.JUnit is not null)
            {
                // CodeBuild shows them under Reports.
                root.Add("reports", new YMap { { "tests", new YMap { { "files", new YSeq([job.JUnit]) }, { "file-format", "JUNITXML" } } } });
            }

            if (job.Environment is null)
            {
                // The next stages get the repository together with what was built.
                root.Add("artifacts", new YMap { { "files", new YSeq(["**/*"]) } });
            }

            files.Add(new(BuildspecPath(job), YamlWriter.Write(root, [.. Header(spec), $"The CodeBuild project of this file uses an image with the tools of {job.Image}."])));
        }

        return files;
    }

    // ---------------------------------------------------------------- TeamCity

    public const string TeamCityPath = ".teamcity/settings.kts";

    public static GeneratedFile TeamCity(PipelineSpec spec)
    {
        var plan = ShellPlan.Create(spec, Platform.TeamCity);
        var text = new StringBuilder();
        text.Append("// Generated by CI/CD Pipeline Generator for ").Append(spec.Name).Append(".\n");
        text.Append("// Before the first run, follow SETUP.md: the project's versioned settings, parameters and agents.\n");
        text.Append("import jetbrains.buildServer.configs.kotlin.*\nimport jetbrains.buildServer.configs.kotlin.buildSteps.script\nimport jetbrains.buildServer.configs.kotlin.triggers.finishBuildTrigger\nimport jetbrains.buildServer.configs.kotlin.triggers.vcs\n\nversion = \"2024.12\"\n\nproject {\n");
        foreach (var job in plan.Jobs.Concat(plan.Rollbacks))
        {
            text.Append("    buildType(").Append(TypeName(job)).Append(")\n");
        }

        text.Append("}\n");
        PlanJob? previous = null;
        var branches = string.Join("\\n", spec.DeployBranches().Select(branch => "+:" + branch));
        foreach (var job in plan.Jobs.Concat(plan.Rollbacks))
        {
            text.Append("\nobject ").Append(TypeName(job)).Append(" : BuildType({\n");
            text.Append("    name = \"").Append(job.Name).Append("\"\n");
            if (job.Environment is not null)
            {
                text.Append("    type = BuildTypeSettings.Type.DEPLOYMENT\n    maxRunningBuilds = 1\n");
            }

            text.Append("    vcs {\n        root(DslContext.settingsRoot)\n    }\n");
            if (job.Keeps.Count > 0 || job.Results.Count > 0)
            {
                // What the deploys take, and the test results and reports kept with the build.
                text.Append("    artifactRules = \"").Append(string.Join("\\n", new[] { job.Keeps.Count > 0 ? "out => out" : null }.Concat(job.Results.Select(folder => $"{folder} => {folder}")).Where(rule => rule is not null))).Append("\"\n");
            }

            if (Names(spec, job).Count > 0)
            {
                // Their values are set in the project (Parameters), as passwords when they are secrets.
                text.Append("    params {\n");
                foreach (var name in Names(spec, job))
                {
                    text.Append("        param(\"env.").Append(name).Append("\", \"%").Append(Scoped(job, name)).Append("%\")\n");
                }

                text.Append("    }\n");
            }

            text.Append("    steps {\n");
            if (job.Services.Count > 0)
            {
                // The agent's own Docker starts the services; the step shares the agent's network to reach them.
                text.Append("        script {\n            name = \"Start the services\"\n            scriptContent = \"\"\"").Append(Kotlin(Services.Start(spec))).Append("\"\"\"\n        }\n");
            }

            text.Append("        script {\n            name = \"").Append(job.Name).Append("\"\n            scriptContent = \"\"\"")
                .Append(Kotlin($"#!/usr/bin/env {Shell(job)}\n" + Script(spec, job, Platform.TeamCity, "export GIT_COMMIT=\"$BUILD_VCS_NUMBER\"")))
                .Append("\"\"\"\n            dockerImage = \"").Append(job.Image).Append("\"\n");
            var run = string.Join(' ', new[] { job.Services.Count > 0 ? "--network host" : null, job.Docker ? "-v /var/run/docker.sock:/var/run/docker.sock" : null }.Where(part => part is not null));
            if (run.Length > 0)
            {
                text.Append("            dockerRunParameters = \"").Append(run).Append("\"\n");
            }

            text.Append("        }\n");
            if (job.Services.Count > 0)
            {
                text.Append("        script {\n            name = \"Stop the services\"\n            executionMode = BuildStep.ExecutionMode.ALWAYS\n            scriptContent = \"\"\"").Append(Kotlin(Services.Stop(spec))).Append("\"\"\"\n        }\n");
            }

            text.Append("    }\n");
            if (job.IsRollback)
            {
                // Started by hand (Run), on the environment that goes back to its previous version.
                text.Append("})\n");
                continue;
            }

            if (previous is null)
            {
                text.Append("    triggers {\n        vcs {\n            branchFilter = \"").Append(spec.BuildPullRequests ? "+:*" : branches).Append("\"\n");
                if (spec.PathFilters.Count > 0)
                {
                    // Only a change under these paths starts a build.
                    text.Append("            triggerRules = \"").Append(string.Join("\\n", spec.PathFilters.Select(path => "+:" + path))).Append("\"\n");
                }

                text.Append("        }\n    }\n");
            }
            else
            {
                // An environment that needs approval is not started by the one before it: someone runs it.
                if (job.Environment is not { RequireApproval: true })
                {
                    text.Append("    triggers {\n        finishBuildTrigger {\n            buildType = \"${").Append(TypeName(previous)).Append(".id}\"\n            successfulOnly = true\n            branchFilter = \"")
                        .Append(spec.Flow == Flow.Tag ? "+:" + spec.TagPattern : job.Environment is null ? branches : "+:" + spec.BranchOf(job.Environment)).Append("\"\n        }\n    }\n");
                }

                text.Append("    dependencies {\n        snapshot(").Append(TypeName(previous)).Append(") {\n        }\n");
                if (job.UsesBuild)
                {
                    text.Append("        artifacts(Build) {\n            artifactRules = \"out => out\"\n        }\n");
                }

                text.Append("    }\n");
            }

            text.Append("})\n");
            previous = job;
        }

        return new(TeamCityPath, text.ToString());
    }

    private static string TypeName(PlanJob job) => string.Concat(job.Id.Split('-', '_').Select(part => part.Length == 0 ? "" : char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>Inside a Kotlin raw string a dollar is written ${'$'}, and TeamCity reads %name% as a parameter, so a percent is doubled.</summary>
    private static string Kotlin(string script) => script.Replace("%", "%%", StringComparison.Ordinal).Replace("$", "${'$'}", StringComparison.Ordinal);

    // ---------------------------------------------------------------- Argo Workflows

    public const string ArgoPath = "argo/workflow-template.yaml";

    public const string ArgoRollbackPath = "argo/rollback-template.yaml";

    /// <summary>The pipeline, or the template started by hand that puts the previous version of one environment back.</summary>
    public static GeneratedFile Argo(PipelineSpec spec, bool rollback = false)
    {
        var plan = ShellPlan.Create(spec, Platform.ArgoWorkflows);
        var jobs = rollback ? plan.Rollbacks : plan.Jobs;
        var tasks = new YSeq { new YMap { { "name", "checkout" }, { "template", "checkout" } } };
        var templates = new YSeq();
        var work = new YSeq { new YMap { { "name", "work" }, { "mountPath", "/work" } } };
        string previous = "checkout";
        foreach (var job in jobs)
        {
            if (job.Environment is { RequireApproval: true } && !rollback)
            {
                // The workflow stops here until someone resumes it (argo resume, or the button in the UI).
                var approve = "approve-" + job.Id;
                tasks.Add(new YMap { { "name", approve }, { "template", "approve" }, { "dependencies", new YSeq([previous]) } });
                previous = approve;
            }

            tasks.Add(new YMap
            {
                { "name", job.Id },
                { "template", job.Id },
                { "dependencies", new YSeq([previous]) },
                { "when", rollback ? $"{{{{workflow.parameters.environment}}}} == {job.Environment!.Name}" : null },
            });
            previous = rollback ? previous : job.Id;
            var env = new YSeq { new YMap { { "name", "HOME" }, { "value", "/work/home" } } };
            foreach (var name in Names(spec, job))
            {
                env.Add(new YMap { { "name", name }, { "valueFrom", new YMap { { "secretKeyRef", new YMap { { "name", $"{spec.Name}-{(Shared(name) ? "build" : job.Environment?.Name ?? "build")}" }, { "key", name } } } } } });
            }

            if (job.Docker)
            {
                env.Add(new YMap { { "name", "DOCKER_HOST" }, { "value", "tcp://127.0.0.1:2375" } });
            }

            var template = new YMap
            {
                { "name", job.Id },
                {
                    "script", new YMap
                    {
                        { "image", job.Image },
                        { "command", new YSeq([Shell(job)]) },
                        { "workingDir", "/work/src" },
                        { "env", env },
                        { "volumeMounts", work },
                        { "source", Braces().Replace(Script(spec, job, Platform.ArgoWorkflows, "mkdir -p \"$HOME\"\nexport GIT_COMMIT=\"$(cat /work/revision)\""), "\"{\"\"{$1}\"\"}\"") },
                    }
                },
            };
            var sidecars = new YSeq();
            foreach (var service in job.Services)
            {
                // Next to the job, in its pod: the tests reach it on localhost.
                var values = new YSeq();
                foreach (var (key, value) in service.Env)
                {
                    values.Add(new YMap { { "name", key }, { "value", value } });
                }

                sidecars.Add(new YMap { { "name", service.Name }, { "image", service.Image }, { "env", values.Count > 0 ? values : null } });
            }

            if (job.Docker)
            {
                // Docker for this job only, next to it.
                sidecars.Add(new YMap
                {
                    { "name", "docker" },
                    { "image", "docker:27-dind" },
                    { "command", new YSeq(["dockerd-entrypoint.sh"]) },
                    { "env", new YSeq { new YMap { { "name", "DOCKER_TLS_CERTDIR" }, { "value", "" } } } },
                    { "securityContext", new YMap { { "privileged", true } } },
                    { "mirrorVolumeMounts", true },
                });
            }

            if (sidecars.Count > 0)
            {
                template.Add("sidecars", sidecars);
            }

            templates.Add(template);
        }

        templates.Insert(0, new YMap
        {
            { "name", "checkout" },
            {
                "script", new YMap
                {
                    { "image", "alpine/git:latest" },
                    { "command", new YSeq(["sh"]) },
                    { "volumeMounts", work },
                    { "source", "set -e\ngit clone --depth 1 --branch \"{{workflow.parameters.revision}}\" \"{{workflow.parameters.repository}}\" /work/src\ngit -C /work/src rev-parse HEAD > /work/revision" },
                }
            },
        });
        templates.Insert(0, new YMap { { "name", "approve" }, { "suspend", new YMap() } });
        templates.Insert(0, new YMap { { "name", "pipeline" }, { "dag", new YMap { { "tasks", tasks } } } });
        var root = new YMap
        {
            { "apiVersion", "argoproj.io/v1alpha1" },
            { "kind", "WorkflowTemplate" },
            { "metadata", new YMap { { "name", (rollback ? "rollback-" : "deploy-") + spec.Name } } },
            {
                "spec", new YMap
                {
                    { "entrypoint", "pipeline" },
                    {
                        "arguments", new YMap
                        {
                            {
                                "parameters", new YSeq
                                {
                                    new YMap { { "name", "repository" } },
                                    new YMap { { "name", "revision" }, { "value", spec.MainBranch } },
                                    rollback ? new YMap { { "name", "environment" }, { "value", spec.Environments[0].Name } } : null,
                                }
                            },
                        }
                    },
                    {
                        "volumeClaimTemplates", new YSeq
                        {
                            new YMap
                            {
                                { "metadata", new YMap { { "name", "work" } } },
                                { "spec", new YMap { { "accessModes", new YSeq(["ReadWriteOnce"]) }, { "resources", new YMap { { "requests", new YMap { { "storage", "5Gi" } } } } } } },
                            },
                        }
                    },
                    { "templates", templates },
                }
            },
        };
        return new(rollback ? ArgoRollbackPath : ArgoPath, YamlWriter.Write(root, rollback
            ? [$"Generated by CI/CD Pipeline Generator for {spec.Name}.", "Submit it by hand with the parameter environment to put the previous version back."]
            : Header(spec)));
    }

    public const string ArgoCronPath = "argo/cron-workflow.yaml";

    /// <summary>The schedule of the pipeline: a CronWorkflow that submits the template.</summary>
    public static GeneratedFile ArgoCron(PipelineSpec spec)
    {
        var root = new YMap
        {
            { "apiVersion", "argoproj.io/v1alpha1" },
            { "kind", "CronWorkflow" },
            { "metadata", new YMap { { "name", "deploy-" + spec.Name } } },
            {
                "spec", new YMap
                {
                    { "schedule", new YScalar(spec.Schedule!.Trim(), Raw: false) },
                    { "timezone", "Etc/UTC" },
                    // A run that is still going is left alone; the next one waits for its own time.
                    { "concurrencyPolicy", "Forbid" },
                    {
                        "workflowSpec", new YMap
                        {
                            { "workflowTemplateRef", new YMap { { "name", "deploy-" + spec.Name } } },
                            {
                                "arguments", new YMap
                                {
                                    {
                                        "parameters", new YSeq
                                        {
                                            new YMap { { "name", "repository" }, { "value", string.IsNullOrWhiteSpace(spec.Repository) ? "<clone URL>" : $"https://github.com/{spec.Repository}.git" } },
                                            new YMap { { "name", "revision" }, { "value", spec.MainBranch } },
                                        }
                                    },
                                }
                            },
                        }
                    },
                }
            },
        };
        return new(ArgoCronPath, YamlWriter.Write(root, [$"Generated by CI/CD Pipeline Generator for {spec.Name}.", "Runs the pipeline on its schedule; change the repository's address when it is not on GitHub."]));
    }

    private static string[] Header(PipelineSpec spec) =>
    [
        $"Generated by CI/CD Pipeline Generator for {spec.Name}.",
        "Before the first run, follow SETUP.md: variables, secrets and what the system itself decides.",
    ];

    // Argo fills in {{...}}; a Docker format such as '{{.Id}}' is written so the shell puts the braces together.
    [GeneratedRegex(@"'\{\{([^}']*)\}\}'")]
    private static partial Regex Braces();
}
