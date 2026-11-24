using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Application.Import;

public sealed class ImportException(string message) : Exception(message);

/// <summary>
/// Reads a pipeline file someone wrote (GitHub Actions, Azure DevOps or GitLab CI/CD) and works out the settings
/// that would write one like it. It is a best guess from the triggers, the environments and the commands: the result
/// is shown as a proposal, with what was assumed and what was not kept, and nothing changes until it is approved.
/// </summary>
public interface IPipelineImporter
{
    /// <summary>related: the other files of the pipeline (templates, included files), read for their commands.</summary>
    PipelineDraft Import(string yaml, IReadOnlyList<string>? related = null);
}
