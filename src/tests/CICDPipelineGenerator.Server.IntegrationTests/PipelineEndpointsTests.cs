using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CICDPipelineGenerator.Server.IntegrationTests;

/// <summary>Real HTTP requests to every endpoint, with the AI off.</summary>
public sealed class PipelineEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public PipelineEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Ai:Provider", "None");
        });
    }

    [Fact]
    public async Task The_examples_are_listed_and_each_one_is_served()
    {
        using var client = _factory.CreateClient();
        var names = await client.GetFromJsonAsync<string[]>(new Uri("/api/examples", UriKind.Relative));
        Assert.NotNull(names);
        Assert.NotEmpty(names);

        foreach (var name in names)
        {
            var example = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/example?name={name}", UriKind.Relative));
            Assert.False(string.IsNullOrWhiteSpace(example.GetProperty("name").GetString()));
        }
    }

    [Fact]
    public async Task A_new_pipeline_comes_empty_and_says_what_is_missing()
    {
        using var client = _factory.CreateClient();
        var spec = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/example?name=new", UriKind.Relative));
        Assert.Equal("", spec.GetProperty("name").GetString());

        using var generated = await client.PostAsJsonAsync(new Uri("/api/generate", UriKind.Relative), spec);
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var body = await generated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("messages").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Every_example_generates_files_without_errors()
    {
        using var client = _factory.CreateClient();
        var names = await client.GetFromJsonAsync<string[]>(new Uri("/api/examples", UriKind.Relative));

        foreach (var name in names!)
        {
            var example = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/example?name={name}", UriKind.Relative));
            using var generated = await client.PostAsJsonAsync(new Uri("/api/generate", UriKind.Relative), example);
            Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
            var body = await generated.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("files").GetArrayLength() > 0, name);
        }
    }

    [Fact]
    public async Task Export_downloads_a_zip_with_the_pipelines_and_the_settings_file()
    {
        using var client = _factory.CreateClient();
        var example = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/example", UriKind.Relative));

        using var export = await client.PostAsJsonAsync(new Uri("/api/export", UriKind.Relative), example);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(await export.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, entry => entry.FullName == ".cicd-pipeline-generator.json");
        Assert.True(zip.Entries.Count > 1);
    }

    [Fact]
    public async Task With_the_ai_off_the_status_says_so_and_propose_answers_502()
    {
        using var client = _factory.CreateClient();
        var status = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/ai/status", UriKind.Relative));
        Assert.False(status.GetProperty("enabled").GetBoolean());

        using var propose = await client.PostAsJsonAsync(new Uri("/api/ai/propose", UriKind.Relative), new { description = "A .NET web app on Azure App Service" });
        Assert.Equal(HttpStatusCode.BadGateway, propose.StatusCode);
    }

    [Fact]
    public async Task Propose_rejects_an_empty_description()
    {
        using var client = _factory.CreateClient();
        using var propose = await client.PostAsJsonAsync(new Uri("/api/ai/propose", UriKind.Relative), new { description = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, propose.StatusCode);
    }

    [Fact]
    public async Task An_unknown_api_path_answers_404()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/api/nothing-here", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("dotnet-app-service", "appService", "dotNet")]
    [InlineData("node-static-web-app", "staticWebApp", "node")]
    [InlineData("windows-service", "windowsService", "dotNet")]
    [InlineData("terraform", "terraform", "none")]
    public async Task A_generated_file_of_every_system_is_read_back_into_its_settings(string example, string target, string stack)
    {
        using var client = _factory.CreateClient();
        var spec = System.Text.Json.Nodes.JsonNode.Parse(await client.GetStringAsync(new Uri($"/api/example?name={example}", UriKind.Relative)))!;
        spec["platforms"] = new System.Text.Json.Nodes.JsonArray("gitHubActions", "azureDevOps", "gitLab");
        using var generated = await client.PostAsJsonAsync(new Uri("/api/generate", UriKind.Relative), spec);
        var files = (await generated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("files").EnumerateArray().Where(file => file.GetProperty("path").GetString()!.EndsWith(".yml", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, files.Count);

        foreach (var file in files)
        {
            using var imported = await client.PostAsJsonAsync(new Uri("/api/import", UriKind.Relative), new { content = file.GetProperty("content").GetString() });
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            var proposal = await imported.Content.ReadFromJsonAsync<JsonElement>();
            var path = file.GetProperty("path").GetString();
            Assert.Equal(target, proposal.GetProperty("spec").GetProperty("deploy").GetProperty("target").GetString());
            Assert.Equal(stack, proposal.GetProperty("spec").GetProperty("build").GetProperty("stack").GetString());
            Assert.Equal(["dev", "test", "prod"], proposal.GetProperty("spec").GetProperty("environments").EnumerateArray().Select(environment => environment.GetProperty("name").GetString()));
            Assert.Equal("promotion", proposal.GetProperty("spec").GetProperty("flow").GetString());
            Assert.Equal("main", proposal.GetProperty("spec").GetProperty("mainBranch").GetString());
            Assert.True(proposal.GetProperty("review").GetArrayLength() > 0, path);
        }
    }

    [Theory]
    [InlineData("travisCi")]
    [InlineData("cloudBuild")]
    [InlineData("codePipeline")]
    [InlineData("teamCity")]
    [InlineData("argoWorkflows")]
    public async Task A_generated_file_of_the_systems_added_later_is_read_back(string platform)
    {
        using var client = _factory.CreateClient();
        var spec = System.Text.Json.Nodes.JsonNode.Parse(await client.GetStringAsync(new Uri("/api/example?name=dotnet-app-service", UriKind.Relative)))!;
        spec["platforms"] = new System.Text.Json.Nodes.JsonArray(platform);
        using var generated = await client.PostAsJsonAsync(new Uri("/api/generate", UriKind.Relative), spec);
        var files = (await generated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("files").EnumerateArray().Where(file => file.GetProperty("path").GetString() != "SETUP.md").Select(file => file.GetProperty("content").GetString()!).ToList();

        using var imported = await client.PostAsJsonAsync(new Uri("/api/import", UriKind.Relative), new { content = files[0], related = files.Skip(1) });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var proposal = (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("spec");
        Assert.Equal(platform, proposal.GetProperty("platforms")[0].GetString());
        Assert.Equal("appService", proposal.GetProperty("deploy").GetProperty("target").GetString());
        Assert.Equal("dotNet", proposal.GetProperty("build").GetProperty("stack").GetString());
        Assert.Equal(["dev", "test", "prod"], proposal.GetProperty("environments").EnumerateArray().Select(environment => environment.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("gitHubActions")]
    [InlineData("azureDevOps")]
    [InlineData("gitLab")]
    public async Task Import_reads_what_the_build_does_besides_compiling_and_how_the_new_version_takes_over(string platform)
    {
        using var client = _factory.CreateClient();
        var spec = System.Text.Json.Nodes.JsonNode.Parse(await client.GetStringAsync(new Uri("/api/example?name=dotnet-app-service", UriKind.Relative)))!;
        spec["platforms"] = new System.Text.Json.Nodes.JsonArray(platform);
        var build = spec["build"]!;
        build["version"] = "9.0";
        build["services"] = new System.Text.Json.Nodes.JsonArray("postgres", "redis");
        build["alsoTestWith"] = new System.Text.Json.Nodes.JsonArray("8.0");
        build["codeScan"] = true;
        build["securityScan"] = true;
        build["sbom"] = true;
        build["coverage"] = true;
        build["publishTestResults"] = true;
        build["cachePackages"] = true;
        build["sonarProjectKey"] = "contoso_orders";
        spec["pathFilters"] = new System.Text.Json.Nodes.JsonArray("src/**");
        spec["notifyOnFailure"] = true;
        spec["deploy"]!["strategy"] = "blueGreen";
        foreach (var environment in spec["environments"]!.AsArray())
        {
            environment!["slot"] = "staging";
        }

        using var generated = await client.PostAsJsonAsync(new Uri("/api/generate", UriKind.Relative), spec);
        var result = await generated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(result.GetProperty("messages").EnumerateArray(), message => message.GetProperty("severity").GetString() == "error");
        var file = result.GetProperty("files")[0].GetProperty("content").GetString();

        using var imported = await client.PostAsJsonAsync(new Uri("/api/import", UriKind.Relative), new { content = file });
        var proposal = (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("spec");
        var read = proposal.GetProperty("build");
        Assert.Equal("9.0", read.GetProperty("version").GetString());
        Assert.Equal(["postgres", "redis"], read.GetProperty("services").EnumerateArray().Select(service => service.GetString()));
        Assert.Equal(["8.0"], read.GetProperty("alsoTestWith").EnumerateArray().Select(version => version.GetString()));
        Assert.True(read.GetProperty("codeScan").GetBoolean());
        Assert.True(read.GetProperty("securityScan").GetBoolean());
        Assert.True(read.GetProperty("sbom").GetBoolean());
        Assert.True(read.GetProperty("coverage").GetBoolean());
        Assert.True(read.GetProperty("publishTestResults").GetBoolean());
        Assert.True(read.GetProperty("cachePackages").GetBoolean());
        Assert.False(read.GetProperty("codeQl").GetBoolean());
        Assert.Equal("contoso_orders", read.GetProperty("sonarProjectKey").GetString());
        Assert.Equal("blueGreen", proposal.GetProperty("deploy").GetProperty("strategy").GetString());
        Assert.True(proposal.GetProperty("notifyOnFailure").GetBoolean());
        if (platform != "gitLab")
        {
            Assert.Equal(["src/**"], proposal.GetProperty("pathFilters").EnumerateArray().Select(path => path.GetString()));
        }
    }

    [Fact]
    public async Task Import_reads_the_names_of_an_app_service_and_says_what_it_does_not_keep()
    {
        const string Workflow = """
            name: Deploy
            on:
              push:
                branches: [release]
              schedule:
                - cron: '0 6 * * 1-5'
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v5
                  - uses: some-org/custom-scan@v2
                  - run: dotnet publish src/Shop/Shop.csproj -c Release -o out/app
              deploy:
                needs: build
                if: github.ref == 'refs/heads/release'
                runs-on: ubuntu-latest
                environment: Production
                steps:
                  - uses: azure/webapps-deploy@v3
                    with:
                      app-name: app-shop-prod
                      slot-name: staging
            """;
        using var client = _factory.CreateClient();
        using var imported = await client.PostAsJsonAsync(new Uri("/api/import", UriKind.Relative), new { content = Workflow });
        var proposal = await imported.Content.ReadFromJsonAsync<JsonElement>();
        var spec = proposal.GetProperty("spec");
        Assert.Equal("release", spec.GetProperty("mainBranch").GetString());
        Assert.Equal("0 6 * * 1-5", spec.GetProperty("schedule").GetString());
        Assert.Equal("src/Shop/Shop.csproj", spec.GetProperty("build").GetProperty("projectPath").GetString());
        var environment = spec.GetProperty("environments")[0];
        Assert.Equal("production", environment.GetProperty("name").GetString());
        Assert.Equal("app-shop-prod", environment.GetProperty("resourceName").GetString());
        Assert.Equal("staging", environment.GetProperty("slot").GetString());
        Assert.Contains(proposal.GetProperty("questions").EnumerateArray(), question => question.GetString()!.Contains("some-org/custom-scan@v2", StringComparison.Ordinal));

        using var broken = await client.PostAsJsonAsync(new Uri("/api/import", UriKind.Relative), new { content = "jobs: [unclosed" });
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        using var empty = await client.PostAsJsonAsync(new Uri("/api/import", UriKind.Relative), new { content = " " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Import_reads_server_names_terraform_state_and_the_files_opened_with_the_pipeline()
    {
        using var client = _factory.CreateClient();
        async Task<JsonElement> ImportExample(string example, string platform, Action<System.Text.Json.Nodes.JsonNode>? change = null)
        {
            var spec = System.Text.Json.Nodes.JsonNode.Parse(await client.GetStringAsync(new Uri($"/api/example?name={example}", UriKind.Relative)))!;
            spec["platforms"] = new System.Text.Json.Nodes.JsonArray(platform);
            change?.Invoke(spec);
            using var generated = await client.PostAsJsonAsync(new Uri("/api/generate", UriKind.Relative), spec);
            var file = (await generated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("files")[0].GetProperty("content").GetString();
            using var imported = await client.PostAsJsonAsync(new Uri("/api/import", UriKind.Relative), new { content = file });
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            return (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("spec");
        }

        var service = await ImportExample("windows-service", "gitHubActions");
        var prod = service.GetProperty("environments")[2];
        Assert.Equal("Billing.Worker", prod.GetProperty("resourceName").GetString());
        Assert.Equal("app-prod", prod.GetProperty("serverLabel").GetString());

        var terraform = await ImportExample("terraform", "gitLab");
        Assert.Equal("infra", terraform.GetProperty("deploy").GetProperty("infraDirectory").GetString());
        Assert.Equal("1.9.8", terraform.GetProperty("deploy").GetProperty("terraformVersion").GetString());
        Assert.Equal("rg-terraform-state", terraform.GetProperty("deploy").GetProperty("stateResourceGroup").GetString());
        Assert.Equal("contosotfstate", terraform.GetProperty("deploy").GetProperty("stateStorageAccount").GetString());

        var bitbucket = await ImportExample("dotnet-app-service", "bitbucket");
        Assert.Equal("bitbucket", bitbucket.GetProperty("platforms")[0].GetString());
        Assert.Equal("appService", bitbucket.GetProperty("deploy").GetProperty("target").GetString());
        Assert.Equal(["dev", "test", "prod"], bitbucket.GetProperty("environments").EnumerateArray().Select(environment => environment.GetProperty("name").GetString()));
        var circle = await ImportExample("dotnet-app-service", "circleCi");
        Assert.Equal("circleCi", circle.GetProperty("platforms")[0].GetString());
        Assert.Equal(["dev", "test", "prod"], circle.GetProperty("environments").EnumerateArray().Select(environment => environment.GetProperty("name").GetString()));

        var jenkins = await ImportExample("dotnet-app-service", "jenkins");
        Assert.Equal("jenkins", jenkins.GetProperty("platforms")[0].GetString());
        Assert.Equal("appService", jenkins.GetProperty("deploy").GetProperty("target").GetString());
        Assert.Equal("dotNet", jenkins.GetProperty("build").GetProperty("stack").GetString());
        Assert.Equal("main", jenkins.GetProperty("mainBranch").GetString());
        Assert.Equal(["dev", "test", "prod"], jenkins.GetProperty("environments").EnumerateArray().Select(environment => environment.GetProperty("name").GetString()));

        // A pipeline that hands its work to a template: alone it asks for the template; with it, the steps are read.
        const string Pipeline = """
            trigger:
              branches:
                include: [main]
            stages:
              - stage: Deploy
                jobs:
                  - deployment: Deploy
                    environment: prod
                    strategy:
                      runOnce:
                        deploy:
                          steps:
                            - template: ci/deploy-steps.yml
            """;
        const string Template = """
            steps:
              - script: dotnet publish src/Shop/Shop.csproj -c Release -o out/app
              - script: az webapp deploy --resource-group rg-shop --name app-shop --src-path app.zip --type zip
            """;
        using var alone = await client.PostAsJsonAsync(new Uri("/api/import", UriKind.Relative), new { content = Pipeline });
        var aloneProposal = await alone.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(aloneProposal.GetProperty("questions").EnumerateArray(), question => question.GetString()!.Contains("ci/deploy-steps.yml", StringComparison.Ordinal));
        Assert.Equal("script", aloneProposal.GetProperty("spec").GetProperty("deploy").GetProperty("target").GetString());

        // The template first, as a browser may send them in any order.
        using var together = await client.PostAsJsonAsync(new Uri("/api/import", UriKind.Relative), new { content = Template, related = new[] { Pipeline } });
        var both = (await together.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("spec");
        Assert.Equal("azureDevOps", both.GetProperty("platforms")[0].GetString());
        Assert.Equal("appService", both.GetProperty("deploy").GetProperty("target").GetString());
        Assert.Equal("dotNet", both.GetProperty("build").GetProperty("stack").GetString());
        Assert.Equal("prod", both.GetProperty("environments")[0].GetProperty("name").GetString());
    }

    public void Dispose() => _factory.Dispose();
}
