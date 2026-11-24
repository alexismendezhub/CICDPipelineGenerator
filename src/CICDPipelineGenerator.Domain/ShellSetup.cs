using System.Globalization;
using System.Text;

namespace CICDPipelineGenerator.Domain;

/// <summary>The part of SETUP.md for Jenkins, Bitbucket Pipelines and CircleCI: what to create before the first run.</summary>
public static class ShellSetup
{
    public static void Write(StringBuilder text, PipelineSpec spec, Platform platform)
    {
        var plan = ShellPlan.Create(spec, platform);
        var build = plan.Jobs.Where(job => job.Environment is null).SelectMany(job => job.Variables).Distinct().ToList();
        var deploy = plan.Jobs.Concat(plan.Rollbacks).Where(job => job.Environment is not null).SelectMany(job => job.Variables).Distinct().ToList();
        if (spec.NotifyOnFailure || spec.NotifyOnSuccess)
        {
            build.Add(JenkinsGenerator.NotifyCredential);
        }

        var environments = string.Join(", ", spec.Environments.Select(environment => $"`{environment.Name}`"));
        var labels = plan.Jobs.Concat(plan.Rollbacks).Where(job => job.Label is not null).Select(job => $"`{job.Label}`").Distinct().ToList();
        var step = 1;
        text.AppendLine(CultureInfo.InvariantCulture, $"## {Systems.Name(platform)}");
        text.AppendLine();
        switch (platform)
        {
            case Platform.Jenkins:
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Commit `{JenkinsGenerator.PathFor(spec)}` and create a **Multibranch Pipeline** job for the repository{(spec.SeveralApps ? $", with **Script Path** `{JenkinsGenerator.PathFor(spec)}`" : "")}. It needs the plugins Pipeline, Git, Credentials Binding and Lockable Resources{(plan.Jobs.Any(job => job.JUnit is not null) ? ", and JUnit" : "")}.");
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. The agents have the tools the stages run (the language's tools, and `az`, `aws`, `gcloud`, `kubectl` or `docker` where the deploy uses them). {(labels.Count > 0 ? $"Agents with these labels are used: {string.Join(", ", labels)}. " : "")}{(plan.Jobs.Any(job => job.Windows && job.Label is null) ? "The Windows stages run on an agent with the label `windows`. " : "")}Other stages run on any agent, with bash.");
                if (build.Count + deploy.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. In **Manage Jenkins → Credentials**, add these as **Secret text**, with these IDs:");
                    Variables(text, "the build", build);
                    foreach (var environment in spec.Environments)
                    {
                        Variables(text, environment.Name, deploy.Select(name => Secrets.AzureDevOpsName(environment, name)).ToList());
                    }
                }

                if (spec.Environments.Any(environment => environment.RequireApproval))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. An environment that needs approval waits at **Deploy to <environment>?** until someone with permission to build the job answers.");
                }

                if (plan.Rollbacks.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. For the rollback, create a second **Pipeline** job with **Script Path** `{JenkinsGenerator.RollbackPathFor(spec)}`; run it with **Build with Parameters**.");
                }

                break;
            case Platform.Bitbucket:
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Commit `{BitbucketGenerator.Path}` and turn Pipelines on (**Repository settings → Pipelines → Settings**).");
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. In **Repository settings → Deployments**, create the environments {environments} with exactly these names. A step marked manual waits until someone presses **Deploy**; with Premium, the environment can also limit who may.");
                if (build.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. In **Repository settings → Repository variables**, add (secured when they are secrets):");
                    Variables(text, "the build", build);
                }

                if (deploy.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. In **Repository settings → Deployments**, add these variables to each environment ({environments}), secured when they are secrets:");
                    Variables(text, "each environment", deploy);
                }

                if (labels.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Your own runners (**Repository settings → Runners**) carry these labels: {string.Join(", ", labels)}.");
                }

                if (!string.IsNullOrWhiteSpace(spec.Schedule))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Bitbucket keeps schedules outside the file: in **Pipelines → Schedules**, add one for the branch `{spec.MainBranch}` (`{spec.Schedule}`, UTC).");
                }

                if (plan.Rollbacks.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. To roll back, use **Pipelines → Run pipeline**, choose the branch and the pipeline `custom: rollback-<environment>`.");
                }

                break;
            case Platform.TravisCi:
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Commit `{MoreSystems.TravisPath}` and turn the repository on in Travis CI.");
                if (build.Count + deploy.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. In **Settings → Environment Variables**, add these (not shown in the log; limit an environment's variables to its branch when each environment has its own):");
                    Scoped(text, spec, build, deploy);
                }

                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Each stage runs in Docker, in the image with its tools. Travis CI has no approval and no rollback pipeline: protect the branches that deploy, and roll back by running the build of an earlier commit again.");
                break;
            case Platform.CloudBuild:
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Commit `{MoreSystems.CloudBuildPath}` and create a trigger for the repository in **Cloud Build → Triggers**{(spec.Flow == Flow.Tag ? $", on tags like `{spec.TagPattern}`" : $", on the branches {string.Join(", ", spec.DeployBranches().Select(branch => $"`{branch}`"))}")}{(spec.BuildPullRequests ? ", and a second one on pull requests" : "")}. A trigger can ask for approval before the whole build starts, and its **included files** filter the paths.");
                if (build.Count + deploy.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. In **Secret Manager**, create these secrets and let the build's service account read them (**Secret Manager Secret Accessor**):");
                    Scoped(text, spec, build, deploy);
                }

                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Every step runs in one build, one after another; a deploy step ends without doing anything on a branch that does not deploy to its environment.");
                if (plan.Rollbacks.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. To roll back, run `gcloud builds submit --config {MoreSystems.CloudBuildRollbackPath} --substitutions _ENVIRONMENT=<environment>`, or a manual trigger with that file.");
                }

                break;
            case Platform.CodePipeline:
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Commit the buildspec files and create one CodeBuild project per file ({string.Join(", ", plan.Jobs.Select(job => $"`{MoreSystems.BuildspecPath(job)}`"))}), each with an image that has the tools its file names in its first lines{(plan.Jobs.Any(job => job.Docker) ? " and **privileged** mode where it builds an image" : "")}.");
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Create a pipeline in CodePipeline (V2) with the repository as source and one stage per project, in this order; each stage takes the output of the build stage as its input.{(spec.Environments.Any(environment => environment.RequireApproval) ? " Put a **Manual approval** action before the stages of the environments that need it." : "")}{(spec.Flow == Flow.Tag ? $" The pipeline's trigger filters the tags `{spec.TagPattern}`." : "")}");
                if (build.Count + deploy.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. In **Systems Manager → Parameter Store**, create these as **SecureString**, and let each project's role read its own:");
                    foreach (var job in plan.Jobs.Where(job => MoreSystems.Names(spec, job).Count > 0))
                    {
                        Variables(text, job.Name, [.. MoreSystems.Names(spec, job).Select(name => MoreSystems.Parameter(spec, job, name))]);
                    }
                }

                if (plan.Rollbacks.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. To roll back, create a project per rollback file ({string.Join(", ", plan.Rollbacks.Select(job => $"`{MoreSystems.BuildspecPath(job)}`"))}) and start the one of the environment by hand.");
                }

                if (plan.Jobs.Any(job => job.Services.Count > 0))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. The build project starts the services for the tests with Docker: turn its **privileged** mode on.");
                }

                break;
            case Platform.TeamCity:
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Commit `{MoreSystems.TeamCityPath}` and, in the project, turn on **Versioned Settings** with the Kotlin format and the settings taken from the repository. The agents run Docker: every step runs in the image with its tools.");
                if (build.Count + deploy.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. In the project's **Parameters**, add these (type **Password** when they are secrets):");
                    Scoped(text, spec, build, deploy);
                }

                if (plan.Rollbacks.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. To roll back, run the **Roll back <environment>** configuration by hand.");
                }

                if (spec.Environments.Any(environment => environment.RequireApproval))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. A deployment that needs approval has no trigger: someone with permission runs it (**Deploy**) on the build to promote. The **Build Approval** feature can also ask for it.");
                }

                break;
            case Platform.ArgoWorkflows:
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Apply `{MoreSystems.ArgoPath}` in the namespace of Argo Workflows (`kubectl apply -n argo -f {MoreSystems.ArgoPath}`) and start it with `argo submit --from workflowtemplate/deploy-{spec.Name} -p repository=<clone URL> -p revision=<branch or tag>`, or from Argo Events on a push.");
                if (build.Count + deploy.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Create these Kubernetes secrets in the same namespace, with these keys:");
                    Variables(text, $"`{spec.Name}-build`", build);
                    foreach (var environment in spec.Environments)
                    {
                        Variables(text, $"`{spec.Name}-{environment.Name}`", deploy);
                    }
                }

                if (plan.Rollbacks.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. To roll back, apply `{MoreSystems.ArgoRollbackPath}` too and run `argo submit --from workflowtemplate/rollback-{spec.Name} -p repository=<clone URL> -p environment=<environment>`.");
                }

                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. A private repository needs credentials in the clone URL or a mounted key in the `checkout` template.{(spec.Environments.Any(environment => environment.RequireApproval) ? " An environment that needs approval stops the workflow until someone resumes it (`argo resume`)." : "")}{(plan.Jobs.Any(job => job.Docker) ? " The jobs that build an image run Docker next to them, which needs privileged pods." : "")}");
                break;
            default:
                text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Commit `{CircleCiGenerator.Path}` and set the project up in CircleCI.");
                if (deploy.Count > 0 || spec.Environments.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. In **Organization settings → Contexts**, create one context per environment ({string.Join(", ", spec.Environments.Select(environment => $"`{CircleCiGenerator.Context(spec, environment)}`"))}){(deploy.Count > 0 ? " with these variables:" : "; the deploy jobs use them, and a context can limit who may run them.")}");
                    if (deploy.Count > 0)
                    {
                        Variables(text, "each environment's context", deploy);
                    }
                }

                if (build.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Create the context `{CircleCiGenerator.Context(spec, null)}` for the build, with:");
                    Variables(text, "the build", build);
                }

                if (spec.Environments.Any(environment => environment.RequireApproval))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. An environment that needs approval waits at its **approve-<environment>** job until someone approves it in the workflow.");
                }

                if (labels.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. Your own runners use these resource classes (`namespace/name`): {string.Join(", ", labels)}.");
                }

                if (plan.Rollbacks.Count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{step++}. To roll back, use **Trigger pipeline** with the parameter `rollback` set to the environment.");
                }

                break;
        }

        text.AppendLine();
        Identity(text, spec, platform);
    }

    /// <summary>The names in a system with one list for everything: an environment's carry its name.</summary>
    private static void Scoped(StringBuilder text, PipelineSpec spec, List<string> build, List<string> deploy)
    {
        Variables(text, "the build", build);
        foreach (var environment in spec.Environments)
        {
            Variables(text, environment.Name, [.. deploy.Select(name => Secrets.AzureDevOpsName(environment, name))]);
        }
    }

    private static void Variables(StringBuilder text, string scope, List<string> names)
    {
        if (names.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"   - {scope}: {string.Join(", ", names.Select(name => $"`{name}`"))}");
        }
    }

    /// <summary>How the system signs in to the cloud the pipeline deploys to.</summary>
    private static void Identity(StringBuilder text, PipelineSpec spec, Platform platform)
    {
        var name = Systems.Name(platform);
        if ((platform == Platform.CloudBuild && spec.IsGcp) || (platform == Platform.CodePipeline && spec.IsAws))
        {
            text.AppendLine(platform == Platform.CloudBuild
                ? "Cloud Build runs as the service account of its trigger: give that account the roles the deploy needs. No key is stored."
                : "Each CodeBuild project runs with its service role: give the role of each environment's project the permissions the deploy needs. No key is stored.");
            text.AppendLine();
            return;
        }

        if (spec.NeedsAzure)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{name} signs in to Azure as a service principal with a client secret: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` and `AZURE_CLIENT_SECRET` (`az ad sp create-for-rbac` writes them; give it the roles listed above). Microsoft Entra matches a federated credential's subject exactly, and the subject of this system's tokens changes with every run, so there is no sign-in without a stored secret here; renew the secret before it expires.");
            text.AppendLine();
        }
        else if (spec.IsAws)
        {
            text.AppendLine(platform switch
            {
                Platform.Bitbucket => "Bitbucket signs in to AWS with OpenID Connect: **Repository settings → OpenID Connect** shows the identity provider URL and the audience to add in IAM; let the role trust the repository (the `sub` claim starts with the repository's UUID) and keep its ARN in `AWS_ROLE_ARN`.",
                Platform.CircleCi => "CircleCI signs in to AWS with OpenID Connect: add the identity provider `https://oidc.circleci.com/org/<organization-id>` (audience: the organization ID) in IAM; let the role trust the project (`sub` like `org/<organization-id>/project/<project-id>/user/*`) and keep its ARN in `AWS_ROLE_ARN`.",
                _ => $"{name} signs in to AWS with an access key: create an IAM user per environment and keep its key in `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY`.",
            });
            text.AppendLine();
        }
        else if (spec.IsGcp)
        {
            text.AppendLine(platform switch
            {
                Platform.Bitbucket => "Bitbucket signs in to Google Cloud with workload identity federation: create a pool and provider with the issuer and audience shown in **Repository settings → OpenID Connect**, let it impersonate the service account, and keep `GCP_WORKLOAD_IDENTITY_PROVIDER` (as `projects/<number>/locations/global/workloadIdentityPools/<pool>/providers/<provider>`), `GCP_SERVICE_ACCOUNT` and `GCP_PROJECT_ID`.",
                Platform.CircleCi => "CircleCI signs in to Google Cloud with workload identity federation: create a pool and provider for the issuer `https://oidc.circleci.com/org/<organization-id>` (audience: the organization ID), let it impersonate the service account, and keep `GCP_WORKLOAD_IDENTITY_PROVIDER` (as `projects/<number>/locations/global/workloadIdentityPools/<pool>/providers/<provider>`), `GCP_SERVICE_ACCOUNT` and `GCP_PROJECT_ID`.",
                _ => $"{name} signs in to Google Cloud with a service account key: keep the key's JSON in `GCP_CREDENTIALS` and the project in `GCP_PROJECT_ID`.",
            });
            text.AppendLine();
        }
    }
}
