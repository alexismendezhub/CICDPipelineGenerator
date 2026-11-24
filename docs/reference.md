# CI/CD Pipeline Generator: reference

How to use the screen, how the generated pipelines work, the settings, the API and the tests.

## 1. What it does

CI/CD Pipeline Generator generates the pipelines that build, test and deploy your code.

| Deploys to | How |
| --- | --- |
| Azure App Service | builds, keeps the result, uploads it to the web app (optional slot) |
| Azure Functions | builds, uploads the zip to the function app (optional slot) |
| Azure Static Web Apps | builds the site and uploads it with the deployment token |
| Azure Container Apps | runs the tests, builds the Dockerfile in Azure Container Registry, updates the app's image |
| Azure Kubernetes Service | same image build, applies the manifests, sets the new image and waits for the rollout |
| Terraform | checks the code, then plans and applies each environment with its own state file |
| Bicep | checks the template, then previews (what-if) and deploys into each resource group |
| Windows service (your servers) | on each server: stops the service, keeps the current version, copies the new one, starts it, checks it, and puts the previous version back if anything fails |
| IIS site (your servers) | the same, stopping and starting the site's application pool |
| Scheduled task (your servers) | the same, disabling and stopping the task while its folder changes |
| Linux service (your servers) | the same with bash and systemd: `systemctl stop`, `rsync`, `systemctl start`, checks and roll back |
| AWS | S3 (static site), Lambda, Elastic Beanstalk, or ECS with the image built into ECR |
| Google Cloud | Cloud Run, Cloud Functions, App Engine, or GKE with the image built into Artifact Registry |
| Kubernetes anywhere | AKS, GKE, EKS or any cluster reached with its kubeconfig: manifests and `kubectl`, or a **Helm** chart |
| A container registry | builds the image and pushes it to Docker Hub, GitHub Container Registry or another registry; each environment tags it |
| Infrastructure | Terraform on Azure, AWS or Google Cloud (state in a storage account or a bucket), Bicep, and also: ARM templates, AWS CloudFormation, the AWS CDK and Pulumi (Azure, AWS or Google Cloud) |
| Site hosts | Netlify, Vercel, Cloudflare Pages, Firebase Hosting and DigitalOcean App Platform |
| More of AWS | SAM (serverless applications) and App Runner |
| GitOps | pushes the image and writes its tag into the manifests repository that Argo CD or Flux follows |
| Application stores | Google Play (Android), TestFlight (iOS, on macOS agents, optionally sent to App Store review) and the Microsoft Store (MSIX); native projects, React Native or .NET MAUI |
| More of Azure | a database alone (migrations, SQL scripts or a dacpac), API Management (an API from its OpenAPI file), Logic Apps and Data Factory |
| Heroku | pushes the image or the code to the application |
| Azure Service Fabric | uploads the application package to each cluster and creates or upgrades the application |
| Oracle Cloud and other clouds | a Kubernetes cluster by its kubeconfig and any container registry |
| A package feed | packs the project and publishes the NuGet or npm package |
| Your own deploy commands | runs your commands per environment: bash on a hosted runner (optionally signed in to Azure) or PowerShell on the environment's servers |

The code can be .NET, .NET Framework, Node.js, Python, Java (Maven or Gradle), Go, Ruby, PHP, Rust, C or C++ (CMake),
Swift, Scala, Elixir, Flutter (web), an Android or iOS app, a Windows app packaged as MSIX, or your own build commands, built on hosted agents or on your own (Linux,
Windows or macOS). A new version can take over with the target's own update, **blue-green** or **canary**. .NET and Node.js builds can also
restore from **private package feeds** (NuGet, npm, Maven, pip, Gradle, sbt, Go, RubyGems, Composer, Cargo, Hex, pub, Swift registries and Conan): Azure Artifacts, GitHub Packages, JFrog
Artifactory or any other feed by its URL. Every environment can also run **your own commands before and after the deploy**,
get its own **settings and secrets**, and have its **database updated** first (Entity Framework Core migrations or
SQL scripts or a dacpac). A push, a **tag** or a **schedule** can start it, and a failed run can post to a webhook.

## 2. How to run it

**Requirements:** the [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js](https://nodejs.org) 20, 22 or 24,
and PowerShell 7 (`pwsh`). No database is needed: the settings are kept in the browser tab while you work and in the settings file you
save.

### 2.1 Start it

```cmd
scripts\start.cmd
```

It builds the screen, starts the app at http://localhost:5090 and opens your browser. The app listens only on your
computer.

### 2.2 Visual Studio and live reload

Open `CICDPipelineGenerator.slnx` and start **CICDPipelineGenerator.Server**. For live reload while you work on the
screen, run the server (`dotnet run --project src\CICDPipelineGenerator.Server --urls http://localhost:5090`) and, in
`src\cicdpipelinegenerator.client`, run `npm start`. Then open http://localhost:4200. The `/api` calls go to the server.

### 2.3 The AI (optional)

**Describe with AI** uses Claude. Set your API key before you start the app and open a new terminal afterwards:

```powershell
setx ANTHROPIC_API_KEY "your-key"
```

Without a key the form works the same and the Describe button is disabled.

## 3. Project structure

```text
CICDPipelineGenerator.slnx                                ← solution (.slnx format)
├─ src/
│  ├─ CICDPipelineGenerator.Domain/                       ← the settings, the checks, the YAML writer, one generator per
│  │                                                        system, the setup guide, the examples, the AI prompt and
│  │                                                        the proposal builder (no framework dependencies)
│  ├─ CICDPipelineGenerator.Application/                  ← ports: IPipelineProposer + AI options, IPipelineImporter
│  ├─ CICDPipelineGenerator.Infrastructure/               ← implementations
│  │  ├─ Ai/                                              ← the Anthropic (Claude) proposer
│  │  ├─ Import/                                          ← reads an existing pipeline file into proposed settings
│  │  └─ DependencyInjection.cs                           ← AddInfrastructure
│  ├─ CICDPipelineGenerator.Server/                       ← ASP.NET Core (Minimal APIs); serves the screen from wwwroot
│  │  ├─ Endpoints/PipelineEndpoints.cs                   ← every /api endpoint
│  │  ├─ Contracts/                                       ← request records and the settings file format
│  │  └─ Program.cs
│  ├─ cicdpipelinegenerator.client/                       ← Angular screen (.esproj): form, code tabs, AI proposals
│  └─ tests/
│     ├─ CICDPipelineGenerator.Domain.UnitTests/          ← checks, generators (read back as YAML), actionlint, the
│     │                                                     PowerShell deploy run on temporary folders, AI parsing
│     └─ CICDPipelineGenerator.Server.IntegrationTests/   ← the endpoints over HTTP
├─ docs/                                                  ← cloud-publishing.md (open findings), not-generated.md (what the
│                                                           tool does not write), Word and PDF copies
└─ scripts/                                               ← start.cmd (builds the screen and starts the app), build-and-test.cmd
```

## 4. Use it

1. Fill in the four steps (**Project**, **Pipeline systems**, **Deploy to**, **Build**) and the environments, or
   pick one in **Open an example…**. A new tab or window starts empty, and so does **New pipeline**. Private feeds go under **Build → Private package feeds**, and your own commands
   under each environment.
2. The code tabs show every file as you change the form, and **Checks** lists what must be fixed before the YAML
   appears.
3. **Download all (.zip)** gives a ZIP whose files go straight into the root of your repository; **Download this
   file** and **Copy** work on the file you are looking at.
4. **Save settings** saves a `.cicd-pipeline-generator.json` file and **Open settings** brings it back. The ZIP
   includes the same file.
5. **Open a pipeline file** reads a pipeline you already have (GitHub Actions, Azure DevOps, GitLab CI/CD, Bitbucket
   Pipelines, CircleCI, a Jenkinsfile, Travis CI, Cloud Build, CodeBuild, TeamCity or Argo Workflows; choose the templates or included files it uses together with it) and proposes
   the settings that write one like it: the system, the language, the target, the branches, the environments and
   their names. It is a best guess shown as a proposal, with the steps it did not keep listed under **Questions**.

## 5. Proposals: nothing changes until you approve

Press **Describe with AI** and say in your own words what you want to deploy ("a Python API with Docker to Container
Apps, GitHub Actions and GitLab, dev, staging and prod with approval"). Claude proposes the settings:

- **Needs your review** lists every value the AI chose that you did not say (names, versions, environments).
- **Questions** lists what it needs you to answer, and what the app cannot do.
- **Changes** compares the proposal with your current settings, and the code tabs show the proposal's YAML.
- **Ask for changes** corrects the proposal in words; **Approve** puts it in the form; **Discard** drops it.

## 6. How the pipelines work

- **No stored passwords.** GitHub and GitLab sign in to Azure with OpenID Connect (federated credentials);
  Azure DevOps uses workload identity federation service connections.
- **Jenkins, Bitbucket Pipelines and CircleCI.** Their jobs are command lines only (`az`, `aws`, `gcloud`, `kubectl`,
  `docker`), so nothing depends on plugins or orbs. Bitbucket and CircleCI sign in to AWS and Google Cloud with their
  OpenID Connect token; the three sign in to Azure with a service principal secret, and Jenkins keeps every variable
  as a credential. Approvals are a Jenkins input, a manual Bitbucket step or a CircleCI approval job.
- **Travis CI, Cloud Build, CodeBuild, TeamCity and Argo Workflows.** The same jobs, each one as a single bash
  script in a Linux image: stages in `.travis.yml`, steps in `cloudbuild.yaml`, one `buildspec` file per CodeBuild
  project of the pipeline, build configurations in `.teamcity/settings.kts` and a `WorkflowTemplate`. Cloud Build
  and CodeBuild deploy to their own cloud with the identity they already run as. They also get the services for
  the tests, the webhook messages, the test results (CodeBuild reports, TeamCity artifacts) and the rollback
  started by hand. What a system decides itself (triggers, approvals, schedules) is in `SETUP.md`.
- **Services for the tests.** PostgreSQL, MySQL, SQL Server, Redis or MongoDB start next to the build job, and the
  tests find them with `POSTGRES_HOST`, `POSTGRES_PORT` and the like.
- **Other versions.** The tests can also run with other versions of the language, a job each; the deploy waits for
  all of them.
- **A preview per pull request.** An App Service pull request gets its own slot, removed when it closes: a job and
  a clean-up workflow on GitHub Actions (with a comment that says the address), a review app on GitLab. A Container
  Apps pull request gets a revision with no traffic and its own address (GitHub Actions, with blue-green or canary).
- **Shared templates.** With a templates repository, the pipeline is written as a template that takes the
  application's name and each environment's resource names (a reusable workflow on GitHub, an `extends` template on
  Azure DevOps, an included file with inputs on GitLab) and a short file that calls it.
- **Setup in one command.** A script per system, in bash and in PowerShell, does the one-time setup and asks for
  the secrets: `setup/setup-github` for Azure (identities with their federated credentials and roles), AWS (the
  identity provider and a role per environment) or Google Cloud (the workload identity pool and a service account
  per environment), with the environments of the repository and their variables; `setup/setup-azure-devops`
  (identities, service connections with workload identity federation, environments, the pipeline and its secret
  variables); and `setup/setup-gitlab` (identities and the project's variables with their environment scope). On
  Azure they also create what is missing: the resource group and, for App Service, the plan and the web app.
  Nothing is left to do by hand but choosing who approves.
- **Service Fabric.** The application project is packaged with MSBuild on a Windows agent, uploaded to each cluster
  and created or upgraded with that environment's `ApplicationParameters` file.
- **CodeQL.** On GitHub Actions the code can also be analysed with CodeQL, next to the build.
- **Blue-green and canary.** App Service and Functions swap a slot; Container Apps, Cloud Run and Lambda move the
  traffic between revisions or versions; Kubernetes switches a service between two deployments or updates a canary
  deployment first, with manifests or a Helm chart; ECS moves the load balancer with CodeDeploy; Elastic Beanstalk
  swaps the environments' addresses; and with
  your own servers the first one is updated and watched before the rest. A canary gets a share of the traffic for some minutes; when the environment's health address
  does not answer, the previous version keeps the traffic and the run fails.
- **Tests after the deploy.** An environment can run the end-to-end tests of a folder (Playwright) and a load test
  script (k6) against what was just deployed.
- **Checks next to the tests.** The language's formatter and linter, a Semgrep analysis of the code, a Trivy scan (packages with known
  vulnerabilities, secrets left in files, risky Dockerfile and infrastructure settings) that stops the run, a list of
  components (CycloneDX SBOM) kept with the run, and images signed with cosign after the push.
- **Deploy windows.** An environment can change only on some days and hours (UTC); outside them its deploy stops
  before anything changes.
- **Releases and messages.** With tags, GitHub and GitLab publish a release with notes from the commits after the
  last environment; the webhook can also get a message when the last environment is deployed.
- **Application stores.** The Android bundle and the iOS archive are signed with a keystore or a certificate kept as
  secrets; the first environment uploads the build and the next ones promote it (Google Play tracks, TestFlight
  groups, Microsoft Store flights).
- **Environments in order.** Either one branch that deploys dev, then test, then prod (each waiting for the one
  before), or one branch per environment.
- **Approvals.** Any environment can wait for someone to approve it: GitHub required reviewers, Azure DevOps
  environment checks, GitLab manual jobs.
- **Pull requests** only build and test; they never deploy.
- **One deployment at a time** per environment.
- **Your own servers.** Windows services and IIS sites are deployed by the runner or agent already installed on each
  server: GitHub self-hosted runners with the environment's label, the tagged virtual machines of the Azure DevOps
  environment, or GitLab runners with the environment's tag. The whole deploy is one Windows PowerShell script inside
  the YAML: stop (waiting for the process to end), back up to `<folder>.previous`, mirror the build with `robocopy`
  leaving the files you list (`appsettings.Production.json`, `logs`...), start, check that it runs and that the health
  address answers, and roll back when any of that fails.
- **Linux servers.** A systemd service is deployed with one bash script: stop, back up to `<folder>.previous`, copy
  with `rsync` leaving the files you list, start, check, and roll back when any of that fails.
- **Servers without a runner.** A server deploy can also run from a hosted runner over **SSH** (Linux, with the key
  and the known hosts kept as secrets) or **WinRM** over HTTPS (Windows, with a user and password kept as secrets): the build is copied to
  each host and the same script runs there.
- **Infrastructure.** Each tool checks the code first and can show what will change before it changes anything:
  `terraform plan`, Bicep or ARM what-if, a CloudFormation change set, `cdk diff` or `pulumi preview`.
- **AWS and Google Cloud.** GitHub and GitLab sign in with OpenID Connect (an IAM role on AWS, workload identity
  federation on Google Cloud); Azure DevOps reads an AWS access key or a Google service account key kept as
  secret variables. The deploys are the clouds' own CLI commands (`aws`, `gcloud`, `kubectl`).
- **Several servers.** An environment with several servers is updated one server after another, stopping at the
  first that fails: a job per server on GitHub (matrix) and GitLab (parallel matrix), a rolling deployment over the
  environment's virtual machines on Azure DevOps.
- **Where the build runs.** Hosted Linux or Windows, or your own agents by label (GitHub runner label, Azure DevOps
  agent pool, GitLab runner tag). On Windows the build steps are PowerShell. .NET Framework restores (packages.config
  too), builds and publishes with MSBuild, found through `vswhere`, and tests with `vstest.console.exe`.
- **Settings per environment.** Each environment lists values and the names of secrets. They become app settings on
  App Service, Functions and Container Apps, entries written into `appsettings.json` or `Web.config` on Windows
  services and IIS, and environment variables for your own deploy commands. Values and secrets reach the scripts as
  environment variables of the step, never as text inside a command.
- **Database before the deploy.** Entity Framework Core migrations are built as a bundle with the code and run
  against each environment with its `DB_CONNECTION` secret; or every `.sql` file of a folder runs in name order with
  `sqlcmd`. `SETUP.md` lists the secrets to create per environment and system.
- **Tags.** With "a tag deploys", a tag such as `v1.2.3` deploys every environment in order and pushes to the branch
  only build and test.
- **Coverage and SonarQube.** The tests can collect code coverage and publish it, and a SonarQube or SonarCloud
  analysis can run with the build (`SONAR_TOKEN` and `SONAR_HOST_URL`).
- **Build extras.** Keep the test results (.NET, Java, Scala, Go, PHP, Swift, C or C++, Node.js, Python, Ruby, Rust
  and Flutter), fail on packages with known high or critical
  vulnerabilities (.NET, Node.js, Python) and keep the downloaded packages between runs (hosted Linux agents).
- **Rollback by hand.** For Windows services, IIS sites and scheduled tasks, an extra pipeline started by hand puts
  the previous version (`<folder>.previous`) back on the environment you choose.
- **Several applications in one repository.** Each application gets files named after it
  (`deploy-<name>.yml`, `azure-pipelines-<name>.yml`, `ci/<name>.gitlab-ci.yml`) and runs when its own paths change.
- **Your own commands.** Commands before and after the deploy, and whole deploys written by you, run in the same shell
  as the rest of the job and get `DEPLOY_ENVIRONMENT` with the environment's name.
- **Private feeds without stored files.** The pipeline registers each feed before the restore and passes its
  credentials only to the build steps: NuGet reads them from `NuGetPackageSourceCredentials_<name>` and npm from
  `FEED_<NAME>_TOKEN`. Azure DevOps signs in to its own Azure Artifacts feeds with the build identity, GitHub Actions
  reads GitHub Packages with its `GITHUB_TOKEN` and can sign in to JFrog with OpenID Connect; everything else uses a
  token kept as a secret of the pipeline system. `SETUP.md` lists which secrets to create.

## 7. Configuration reference

### 7.1 The AI (Claude)

**Describe with AI** uses Claude. Your description and the current settings are sent to Anthropic. Do not include
passwords or keys.

Create an API key at [platform.claude.com](https://platform.claude.com) and set it before you start CI/CD Pipeline
Generator:

```powershell
setx ANTHROPIC_API_KEY "your-key"
```

Open a new terminal afterwards so it sees the key. The settings live in
`src/CICDPipelineGenerator.Server/appsettings.json` under `Ai`:

| Setting | Default | What it does |
| --- | --- | --- |
| `Provider` | `Anthropic` | `None` turns the AI off (the Describe button is disabled; the form still works) |
| `Model` | `claude-opus-5-5` | The Claude model |
| `Effort` | `medium` | `low`, `medium`, `high`, `xhigh` or `max`: how hard Claude thinks |
| `ApiKey` | empty | Only if you prefer it to the `ANTHROPIC_API_KEY` variable (use user secrets, never commit it) |

Every request asks for a fixed JSON shape (structured output) and turns on Anthropic's server-side fallback, so a
declined request is answered by another Claude model in the same call.

## 8. API

| Method and path | Description |
|---|---|
| `GET /api/examples` | The names of the examples |
| `GET /api/example?name=` | One example's settings |
| `POST /api/generate` | The settings → the pipeline files, `SETUP.md` and the checks |
| `POST /api/export` | The ZIP: every file plus `.cicd-pipeline-generator.json` (400 when the settings have errors) |
| `GET /api/ai/status` | Whether the AI is on, and why not |
| `POST /api/import` | A pipeline file's text (and the files it uses) → proposed settings, what to review and what was not kept (400 when it is empty, too long or not YAML) |
| `POST /api/ai/propose` | Proposed settings from a description (400 for an empty or too long description, 502 when the AI fails) |

Any other `/api/...` path answers 404.

## 9. Tests

`scripts\build-and-test.cmd` runs everything on Windows: `npm audit`, the screen build, the .NET build (warnings as
errors) and the .NET tests.

| Project | Command | Covers |
|---|---|---|
| `CICDPipelineGenerator.Domain.UnitTests` (2363) | `dotnet test CICDPipelineGenerator.slnx` | The checks, the YAML writer and the AI parsing. Every combination of target, language and flow is written for the first six systems and read back (YAML, or the structure of the Jenkinsfile), and for the five added later (YAML read back, every job script checked with `bash -n`, the structure of `settings.kts`), also with commands before and after the deploy; every kind of package feed, several servers, Windows and own build agents, .NET Framework, settings, database migrations and schedules too. `azure-pipelines.yml`, `.gitlab-ci.yml` and `.circleci/config.yml` are checked against the JSON schemas Microsoft, GitLab and CircleCI publish (keywords, value types, task inputs), and `bitbucket-pipelines.yml` against Atlassian's when it was downloaded into `Schemas/`. With [actionlint](https://github.com/rhysd/actionlint) installed (`winget install rhysd.actionlint`), every GitHub workflow is checked the way GitHub reads it. On Windows, every PowerShell script is parsed by Windows PowerShell, and the server copy (files to keep, roll back) and the settings file (JSON and XML) run for real on temporary folders. |
| `CICDPipelineGenerator.Server.IntegrationTests` (21) | same | The endpoints over HTTP with the AI off: every example served and generated, the ZIP with the settings file, AI status and 502, a generated file of each system read back into its settings, a workflow written by hand and a pipeline with its template imported, an empty description refused, unknown `/api` paths answer 404. |

## 10. Production and deployment

The app is built as a tool for one person on their own computer: it listens only on `localhost` and has no sign-in.
Before it faces the internet, the points in [`cloud-publishing.md`](cloud-publishing.md) must be fixed
(sign-in, AI quotas, the API key in a vault). That document also has the target Azure architecture and its cost.

