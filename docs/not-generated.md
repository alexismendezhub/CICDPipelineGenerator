# What CI/CD Pipeline Generator does not write

The tool writes the pipeline files; it does not run deployments. This list keeps what it does not generate yet,
what was decided from documentation instead of a test, and what the generated files were and were not checked
against.

## Not generated

| What | Why it is not there | Meanwhile |
| --- | --- | --- |
| Test results for Elixir, and for Ruby tests that are not RSpec | Elixir's JUnit reporter is a dependency and a setting of the project itself (`junit_formatter` in mix.exs and config), which a pipeline cannot add from outside. RSpec gets its formatter through a second Gemfile. | Add the reporter to the project and keep its file with your own build commands. |
| Test results for a Node.js project whose test tool is not Vitest, Jest or Mocha | The reporter is chosen from package.json; another tool has none the pipeline knows. | The results stay in the log; an empty results file is kept so the run does not fail. |
| CodeQL outside GitHub Actions, and for Swift, Scala, Elixir, Flutter and PHP | CodeQL belongs to GitHub and reads the languages it lists. | Semgrep and Trivy on every system. |
| Signing images built in Azure Container Registry or Cloud Build | cosign signs the images the pipeline pushes with Docker (ECR, Docker Hub, ghcr.io, other registries). | Sign in a command after the deploy. |
| Releases outside GitHub and GitLab; a release without tags | Only those two have a release of their own, and it belongs to a tag. | Your own commands after the deploy. |
| A build matrix in the system's own syntax | The tests with other versions are written as one job per version, which every system reads the same way. | Nothing to do: the result is the same. |
| A preview per pull request for Kubernetes, for Container Apps with the app's own update, and on systems other than GitHub Actions (and GitLab for App Service) | Container Apps gets a revision per pull request only with blue-green or canary, where the traffic is tied to a revision by name: with the app's own update a new revision would take the traffic of production. Kubernetes would need a namespace, an address and registry access per pull request. Azure DevOps and the shell systems have nothing that runs when a pull request closes. | A branch per environment. |
| Shared templates on systems other than GitHub Actions, Azure DevOps and GitLab; parameters other than the name and the resource names | The template takes the application's name and each environment's resource name and group; the language, the target and the branches are part of the template. | One template per kind of application. |
| On Travis CI, Cloud Build, CodeBuild, TeamCity and Argo Workflows: Windows and macOS builds, deploys on the servers' own runners, WinRM and several applications; path filters and schedules on Travis CI, Cloud Build and CodeBuild; a schedule on TeamCity; the package cache outside CodeBuild | Their jobs are one bash script in a Linux image. Triggers belong to the system on those three, TeamCity's cron has its own fields, and a job that runs inside `docker run` has no folder the system keeps. | SETUP.md says where each one is set. TeamCity gets the path filters, Argo Workflows a CronWorkflow, CodeBuild the cache. |
| An approval inside a Travis CI or Cloud Build run; a rollback started by hand on Travis CI | Neither has an approval, and Travis CI has no run with a choice. | Cloud Build: approval on the trigger. Travis CI: protect the branch, and run an earlier commit's build again. |
| The CodePipeline itself and its CodeBuild projects | The tool writes the buildspec files; the pipeline, its stages and roles are resources of your account. | SETUP.md lists the projects and their order. |
| Oracle Cloud with its own command line | A cluster on Oracle Cloud is deployed as "Kubernetes (any cluster)" and its registry as "another registry". | Those two targets, or your own deploy commands with `oci`. |
| A setup script for Azure DevOps or GitLab with AWS or Google Cloud, for the other systems, for infrastructure targets on AWS and Google Cloud, for GitLab with tags, and for several applications in one repository | The scripts cover GitHub Actions with Azure, AWS and Google Cloud, and Azure DevOps and GitLab with Azure. A tag is a ref that changes with every release, which a federated credential cannot name. | Follow SETUP.md. |
| Creating what the deploy needs, other than a resource group and an App Service web app | The setup scripts create those two; a function app, a container app, a cluster or a database have settings only you can choose. | Create them first, or deploy them with the infrastructure targets (Terraform, Bicep). |
| Least-privilege permissions on AWS and Google Cloud | The scripts attach AWS's managed policies and Google's predefined roles for each target, on the account or project. | Narrow them to your resources. |
| An Azure DevOps pipeline created by the script for a repository on GitHub | `az pipelines create` is written for a repository of Azure Repos; one on GitHub needs its own service connection first. | Create the pipeline from Pipelines, New pipeline; the script does the rest. |
| Choosing who approves an environment | Reviewers are people of your organization. | The setup script says where to choose them. |
| Several applications per repository on Bitbucket Pipelines and CircleCI | Both read one file per repository. | One application per repository, or Jenkins, GitHub Actions, Azure DevOps or GitLab. |
| Hosted Windows and macOS on Bitbucket Pipelines | Bitbucket has none. | Your own Windows or macOS runners. |
| A canary on ECS with a share other than a tenth | CodeDeploy moves the traffic with its own configurations (10% for 5 or 15 minutes). | A deployment configuration of your own in CodeDeploy. |
| Every step of an imported file | Opening a file recognises the system, the language and its version, the target, branches, environments and names, the services for the tests, other versions tested, scans, the package cache, test results, private NuGet sources, path filters, webhook messages and blue-green or canary, from the commands; steps it does not know are listed, not copied. Tests after the deploy, deploy windows and settings per environment are not read back. | Add them as your own commands. |

## Decided from documentation, not from a test

- **Azure sign-in on Jenkins, Bitbucket Pipelines and CircleCI uses a service principal secret.** Microsoft Entra
  matches the subject of a federated credential exactly, and the tokens of Bitbucket (the step) and CircleCI (the
  user) carry a subject that changes between runs. No sign-in without a stored secret was tried against Entra from
  those systems; flexible federated credentials may allow it and were not tested.
- **Bitbucket runs a job's script in one shell; Jenkins and CircleCI start a new shell per step.** That is why the
  sign-in is written once on Bitbucket and repeated in every step on the other two.
- **CircleCI's `checkout` in images without git**, the macOS resource class `m4pro.medium` with Xcode `16.4.0`, the
  GitLab macOS image `macos-15-xcode-16` and the Android image `ghcr.io/cirruslabs/android-sdk:35` are taken from
  their documentation.
- **The site hosts' command lines** (`netlify-cli deploy`, `vercel deploy`, `wrangler pages deploy`,
  `firebase-tools deploy`, `doctl apps create-deployment`), `sam deploy`, the App Runner update, the Trivy and
  cosign options and the release commands (`gh release create`, GitLab's `release`) are taken from their
  documentation.
- **Hosted agents have Swift, CMake and sbt**; Elixir and Flutter are installed during the run on Azure DevOps.
- **The five systems added later.** Travis CI's `workspaces` and `if:` conditions; Cloud Build's `script`,
  `automapSubstitutions` and `secretEnv`; CodeBuild's `parameter-store` and `shell: bash`; TeamCity's Kotlin DSL
  (`version = "2024.12"`, `finishBuildTrigger`, `dockerImage`); and Argo's script templates, `suspend`, sidecars and
  `volumeClaimTemplates` are taken from their documentation.
- **Where the tests find a service.** `localhost` on GitHub Actions, Azure DevOps, Bitbucket, CircleCI and Jenkins,
  and the service's name on GitLab, as each system documents. The services hold test data only, so their passwords
  are written in the file.
- **The preview of a pull request** uses `az webapp deployment slot create` and `gh pr comment --edit-last
  --create-if-none`, from their documentation.
- **The feeds added last.** Coursier's `COURSIER_CREDENTIALS` for sbt, `mix hex.repo add --auth-key`, `dart pub token
  add --env-var`, `swift package-registry login --token` and Conan's `remote add` and `remote login` are taken from
  their documentation.
- **JUnit XML for Node.js, Python, Rust and Flutter.** `jest-junit`, Vitest's `junit` reporter,
  `mocha-junit-reporter`, `pytest --junitxml`, `cargo nextest` with a `junit` profile and `junitreport:tojunit` are
  taken from their documentation.
- **CodeQL.** `github/codeql-action` v4, the language names and the build modes (`none`, `autobuild`) are taken from
  GitHub's documentation.
- **Service Fabric.** The `sfctl` commands (`cluster select --pem`, `application upload`, `provision`, `upgrade
  --mode Monitored`, `create`) are taken from their documentation.
- **ECS with CodeDeploy.** `aws deploy create-deployment` with the AppSpec as content, and the configurations
  `CodeDeployDefault.ECSAllAtOnce` and `ECSCanary10Percent5Minutes`, are taken from AWS's documentation.
- **Shared templates.** That a called workflow gets the caller's environments and secrets with `secrets: inherit`,
  that an `extends` template of another repository takes `parameters`, and GitLab's `spec: inputs` with
  `$[[ inputs.name ]]` are taken from their documentation.
- **The review app on GitLab** signs in from the merge request's branch, which needs a flexible federated
  credential in Microsoft Entra (`claimsMatchingExpression`); that was read in the documentation and not tried.
- **Services on the five later systems.** A shared network on Cloud Build (`cloudbuild`), sidecars on Argo, and the
  host's network on Travis CI and TeamCity are taken from their documentation.
- **The Microsoft Store Developer CLI** options (`publish --inputDirectory --appId --flightId`) and **fastlane**
  options (`supply --track_promote_to`, `pilot distribute --groups`) are taken from their documentation.

## Run for real

On 2026-11-22 one case ran from start to finish: a small .NET application in a new private GitHub repository, GitHub
Actions, Azure App Service on Linux (free plan), one environment.

- The setup script ran in bash (Git Bash) and in Windows PowerShell, more than once each. It created the resource
  group's web app and plan, the identity, its federated credential, its role on the web app, the environment of the
  repository and the variables.
- The generated workflow built, tested, signed in to Azure without a stored secret and deployed; the site answered.
- Three things failed first and were fixed in the generator:
  1. In Git Bash, a role scope that starts with `/subscriptions/` was rewritten into a Windows path. The scripts now
     set `MSYS_NO_PATHCONV=1`.
  2. The sign-in was refused (AADSTS700213): repositories created, renamed or transferred since July 2026 carry
     their IDs in the subject (`repo:owner@id/name@id:...`). The scripts and SETUP.md now read the subject's start
     from GitHub (`sub_claim_prefix`).
  3. In Windows PowerShell, `$ErrorActionPreference = 'Stop'` stopped the script at the error text of a command
     that was expected to fail. The scripts check each command's exit code instead.
- The free plan could not be created in East US or East US 2 on the subscription used (no quota); Central US worked.
  The scripts take the location as an argument.
- A second case added what needs a paid plan (Standard S1): blue-green with a slot, and a preview per pull request.
  The deploy went to the slot `staging`, its address answered and the slot was swapped into production. A pull
  request then got its own slot `pr-1` with the changed page, the pull request got the comment with its address,
  production kept its version, and closing the pull request removed the slot.
- Two things to know from that case, neither a fault of the generated files:
  1. The subscription had no quota for S1 in East US, East US 2 or Central US; West US 2 worked. The scripts take
     the location and the plan as arguments.
  2. The first run, started seconds after the setup created the slot, did not find it yet; run again, it passed.
     A new slot takes a moment to be known everywhere.
- A new slot answered with the platform's own page for about a minute after the deploy finished, before the
  application's page.
- A third case, on 2026-11-23, ran Azure DevOps: the same small .NET application in a repository of Azure Repos,
  the setup script in Windows PowerShell and in bash, and App Service on the free plan. The script created the
  identity, the service connection with workload identity federation (issuer and subject read back from Azure
  DevOps), the federated credential, the role, the environment, the pipeline and its permission to use the
  connection and the environment. The pipeline then built, tested and deployed without stopping to ask, and the
  site answered.
- Two things failed first in that case and were fixed in the generator:
  1. In PowerShell, `az` is a .cmd file and stops at a parenthesis inside an argument (`length(...)` in a query).
     The scripts' queries have none now, and the Google Cloud script reads the project number from JSON there.
  2. The first run would have waited for someone to allow the pipeline to use the connection and the environment.
     The script now gives that permission to this pipeline alone.
- The Azure DevOps organization belonged to another Microsoft account than the Azure subscription; the service
  connection worked across the two. A token from `az` was not accepted by git for that organization, so the test
  pushed the files through the REST API; the generated files do not depend on that.
- A fourth case, on 2026-11-23, ran Container Apps on GitHub Actions: the image built in Azure Container Registry,
  the app's own update, then blue-green, then a preview per pull request. Blue-green moved the traffic to the new
  revision and stopped the old one. A pull request got a revision with no traffic and its own address, which showed
  the changed page while production kept its version; a production deploy made while that preview was up moved the
  traffic between the right revisions and left the preview alone; closing the pull request stopped the preview.
- Two things came out of that case and were fixed in the generator:
  1. The app's own update reported success while the new revision was still starting. The deploy now waits until
     the new revision is healthy, and fails when it is not.
  2. Blue-green was refused (`LinkedAuthorizationFailed`): changing the revision mode joins the app to its
     environment again, which needs `Microsoft.App/managedEnvironments/join/action`. The setup script and SETUP.md
     now give the identity Contributor on the Container Apps environment too.
  Blue-green also chose "the revision that has the traffic" as the latest active one, which a preview would have
  been; it now reads it from the traffic itself.
- In that case the registry, the Container Apps environment and the app were created by hand first (the setup
  scripts create a resource group and an App Service web app only), and the app pulled from the registry with its
  own identity.
- The resource group was deleted after each case.

Nothing else on this page has run: the other systems, clouds, targets and scripts are checked as described below.

## How the generated files are checked

- Every combination of target, language and flow is generated for the first six systems and read back (YAML, or the
  structure of the Jenkinsfile: braces, parentheses and strings).
- `azure-pipelines.yml`, `.gitlab-ci.yml` and `.circleci/config.yml` are validated against the JSON schemas
  Microsoft, GitLab and CircleCI publish; `bitbucket-pipelines.yml` against Atlassian's when the file was downloaded
  (it has no licence, so it is not in the repository); GitHub workflows are checked with actionlint.
- Every PowerShell script is parsed by Windows PowerShell. The server copy (files to keep, roll back) and the
  settings file (JSON and XML) run on temporary folders.
- Every bash script is checked with `bash -n`. The Linux service script ran with `sudo` and `systemctl` replaced by
  stand-ins: files to keep, roll back after a failed start, roll back by hand and the settings file.
- For Travis CI, Cloud Build, CodeBuild and Argo Workflows the YAML is read back and every job's script is checked
  with `bash -n`; no schema of theirs is used. `.teamcity/settings.kts` is checked for its structure only (one build
  type per job, braces, and every `$` and `%` of a script escaped): it was not compiled by Kotlin.
- A shared template filled in with the caller's values gives the same GitHub jobs as the pipeline written whole;
  the template and its caller pass actionlint, and Azure DevOps's and GitLab's callers pass their schemas.
- Opening a file is also checked with the files generated for the five later systems.
- Opening a YAML file is checked with files the tool generated for five systems, a GitHub workflow written by hand
  and an Azure DevOps pipeline with a template.

## Not checked

- Generated pipelines have run on GitHub Actions (App Service and Container Apps) and Azure DevOps (App Service)
  only (see "Run for real"); none has run on the other nine systems.
- GitLab is the next one to run for real: its setup script and a pipeline, with a gitlab.com account signed in
  with `glab`. It is pending. A Jenkinsfile was not validated by a Jenkins
  server, `settings.kts` by a TeamCity server, or the WorkflowTemplate by a cluster.
- Services for the tests, the tests with other versions and the preview per pull request have not run.
- Shared templates have not been called from another repository on any system.
- Of the setup scripts, GitHub Actions with Azure and Azure DevOps with Azure have run (see "Run for real"). The
  others (AWS, Google Cloud, GitLab) were checked with `bash -n` and the PowerShell parser only. The role per target, the `gh api` call that creates an
  environment, the service connection written for `az devops service-endpoint create` (scheme
  `WorkloadIdentityFederation`, creation mode `Manual`, and the issuer and subject read back from it), the
  environments created with `az devops invoke` and the `glab variable set` options are taken from documentation.
- The Service Fabric package step (`msbuild -t:Package`) has not packaged a real application.
- CodeQL, the new feeds, the JUnit reporters, Service Fabric and CodeDeploy on ECS have not run.
- End-to-end and load test stages, deploy windows, the database, API Management, Logic Apps, Data Factory and
  Heroku targets have not run against a real service.
- The Windows service, IIS and scheduled task scripts have not run against a real service, site or task; the Linux
  script has not run against a real systemd.
- Remote deploys over SSH and WinRM have not connected to a real server.
- The AWS and Google Cloud commands and their sign-in (OpenID Connect, service connections) have not run against an
  account; neither have CloudFormation, the CDK and Pulumi.
- Kubernetes (manifests, Helm, EKS, a kubeconfig) has not run against a cluster, and no image was pushed to a registry.
- Blue-green and canary deploys have not moved real traffic on any target.
- Android, iOS and MSIX builds have not built a real app, and nothing was uploaded to Google Play, TestFlight or
  the Microsoft Store.
- Gradle, Go, Ruby, PHP, Rust, C and C++, Swift, Scala, Elixir and Flutter builds have not built a real project.
- Terraform on AWS and Google Cloud, the site hosts, SAM, App Runner, DigitalOcean and GitOps have not deployed
  anything; no scan, SBOM, image signature, release or webhook message was produced in a real run.
- SonarQube analysis and the coverage reports have not run against a server or a real project.
- The .NET Framework build (MSBuild, vstest) has not built a real project.
- Entity Framework Core bundles, SQL scripts and dacpac publishing have not run against a database.
- Private feeds (Azure Artifacts, GitHub Packages, JFrog) have not been used against a real feed, to restore or to publish.
- Opening a YAML file has not been tried with pipelines from real projects.
- On GitLab, an application started as a child pipeline (several applications in one repository) and tag deploys
  that sign in to Azure depend on project settings described in SETUP.md and were not run.
- On Azure DevOps, a trigger with only `tags` is taken from the documentation and was not run.

## Open finding

- One test (`ShellSystemsTests.Every_combination_is_written_for_the_three_shell_systems`, target Script, .NET,
  promotion) failed once in a full run on 2026-11-22 and passed alone and in the next full runs; its message was not
  captured. The likely cause is the JSON schema library, which keeps shared tables while tests run at once: the
  schema checks now take turns. That is a likely cause, not a proven one; if the test fails again, run the suite
  with `--logger "console;verbosity=detailed"` to keep the message.
