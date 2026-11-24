# CI/CD Pipeline Generator

**Generate CI/CD pipelines the easy way.** CI/CD Pipeline Generator writes the deployment pipelines for
**GitHub Actions**, **Azure DevOps**, **GitLab CI/CD**, **Jenkins**, **Bitbucket Pipelines**, **CircleCI**,
**Travis CI**, **Google Cloud Build**, **AWS CodePipeline** (CodeBuild), **TeamCity** and **Argo Workflows**, so you
do not have to write them by hand. You also get a `SETUP.md` with the one-time steps (cloud identity, variables,
environments and approvals).

The pipelines build, test and deploy the code through your environments (for example dev, test and prod, with
approval). They deploy to **Azure** (App Service, Functions, Static Web Apps, Container Apps, AKS and more), **AWS**,
**Google Cloud**, any **Kubernetes** cluster, your own **Windows and Linux servers**, container registries, package
feeds and the application stores, and they also run **Terraform** and **Bicep**. The code can be .NET, Node.js,
Python, Java, Go and many other languages. You can also open a pipeline you already have and get its settings back.

## How to use it

You can tell it what to build in two ways:

- **Fill in the form:** choose where the code goes, how it is built and which environments it passes through.
- **Describe it:** write what you need in your own words and Claude (AI) proposes the settings.

The code tabs show every file as you change the form, and **Checks** lists what must be fixed before the pipeline
appears. **Download all (.zip)** gives a ZIP whose files go straight into the root of your repository. **Open a
pipeline file** reads a pipeline you already have and proposes the settings that write one like it.

## How to run it

**Requirements:** the [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js](https://nodejs.org) 20, 22 or 24,
and PowerShell 7 (`pwsh`). No database is needed.

```cmd
scripts\start.cmd
```

It builds the screen, starts the app at http://localhost:5090 and opens your browser. The app listens only on your
computer. You can also open `CICDPipelineGenerator.slnx` in Visual Studio and start **CICDPipelineGenerator.Server**.

## Configuration

It needs no configuration to run. The AI is optional: **Describe with AI** uses Claude. Set your API key before you
start the app and open a new terminal afterwards. Without a key the form works the same and the Describe button is
disabled.

```powershell
setx ANTHROPIC_API_KEY "your-key"
```

## Tests

```cmd
scripts\build-and-test.cmd
```

It runs `npm audit`, the screen build, the .NET build (warnings as errors) and the .NET tests.

## License and disclaimer

Licensed under the [Apache License 2.0](LICENSE): anyone may use, modify and distribute it, including commercially.

It is provided as an example, "as is", without warranty of any kind. Review and test it before using it in
production; the authors are not liable for any damage that results from its use.
