using System.IO.Compression;
using System.Text.Json;
using CICDPipelineGenerator.Application.Ai;
using CICDPipelineGenerator.Application.Import;
using CICDPipelineGenerator.Domain;
using CICDPipelineGenerator.Server.Contracts;

namespace CICDPipelineGenerator.Server.Endpoints;

public static class PipelineEndpoints
{
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/examples", () => Examples.Names);
        api.MapGet("/example", (string? name) => Examples.Get(name));
        api.MapPost("/generate", (PipelineSpec spec) => PipelineGenerator.Generate(spec));
        api.MapPost("/export", Export);

        // An existing pipeline file becomes proposed settings; nothing changes until the person approves them.
        api.MapPost("/import", Import);

        // The AI proposes settings from a description; nothing changes until the person approves them on the screen.
        api.MapGet("/ai/status", (IPipelineProposer proposer) => proposer.Status);
        api.MapPost("/ai/propose", ProposeAsync);
        return app;
    }

    private const int MaxImport = 200_000;

    private static IResult Import(ImportRequest request, IPipelineImporter importer)
    {
        var content = request.Content ?? "";
        var related = (request.Related ?? []).Where(file => !string.IsNullOrWhiteSpace(file)).ToList();
        if (string.IsNullOrWhiteSpace(content) || related.Count > 20 || content.Length + related.Sum(file => file.Length) > MaxImport)
        {
            return Results.BadRequest(new { problem = $"Choose the pipeline file (and up to 20 files it uses) with up to {MaxImport} characters in all." });
        }

        try
        {
            return Results.Ok(ProposalBuilder.Build(importer.Import(content, related), request.Baseline));
        }
        catch (ImportException exception)
        {
            return Results.BadRequest(new { problem = exception.Message });
        }
    }

    private static IResult Export(PipelineSpec spec)
    {
        var result = PipelineGenerator.Generate(spec);
        if (result.HasErrors)
        {
            return Results.BadRequest(result.Messages);
        }

        // The files keep their paths (.github/workflows/...), so the zip unpacks straight into the repository.
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in result.Files.Append(new(spec.SeveralApps ? spec.Name + SettingsJson.FileName : SettingsJson.FileName, JsonSerializer.Serialize(spec, SettingsJson.Indented))))
            {
                using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open());
                writer.Write(file.Content);
            }
        }

        buffer.Position = 0;
        return Results.File(buffer, "application/zip", $"{spec.Name}-pipelines.zip");
    }

    private static async Task<IResult> ProposeAsync(ProposalRequest request, IPipelineProposer proposer, CancellationToken cancellationToken)
    {
        var description = request.Description?.Trim() ?? "";
        if (description.Length is 0 or > ProposalPrompt.MaxDescription)
        {
            return Results.BadRequest(new { problem = $"Describe what you want to deploy in 1 to {ProposalPrompt.MaxDescription} characters." });
        }

        try
        {
            var draft = await proposer.ProposeAsync(description, request.StartFresh ? null : request.Current, cancellationToken);
            return Results.Ok(ProposalBuilder.Build(draft, request.StartFresh ? null : request.Baseline ?? request.Current));
        }
        catch (ProposalException exception)
        {
            return Results.Json(new { problem = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
