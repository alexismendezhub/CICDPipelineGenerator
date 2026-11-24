using System.Text.Json;
using System.Text.Json.Serialization;
using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Server.Contracts;

// Current is what the AI starts from (the settings, or the proposal being corrected); Baseline is the settings before any proposal.
public sealed record ProposalRequest(string? Description, PipelineSpec? Current, bool StartFresh, PipelineSpec? Baseline);

/// <summary>The text of a pipeline file to read; Baseline is the current settings, to list what would change.</summary>
public sealed record ImportRequest(string? Content, PipelineSpec? Baseline, IReadOnlyList<string>? Related = null);

public static class SettingsJson
{
    // The settings file saved in the ZIP: readable in a text editor, enums as camelCase text.
    public static readonly JsonSerializerOptions Indented = Create();

    // The name of the settings file in the ZIP; opening it on the screen brings the settings back.
    public const string FileName = ".cicd-pipeline-generator.json";

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
