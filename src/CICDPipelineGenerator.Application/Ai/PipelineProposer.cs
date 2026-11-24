using System.Text.Json.Nodes;
using CICDPipelineGenerator.Domain;

namespace CICDPipelineGenerator.Application.Ai;

// Ai:Provider is "Anthropic" (Claude) or "None" (the Describe button is off). Ai:Model and Ai:Effort tune the call.
public sealed class AiOptions
{
    public string Provider { get; set; } = "Anthropic";
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "medium";
    public string? ApiKey { get; set; }

    // Only to send the calls somewhere else (a proxy or a local test double); empty means Anthropic.
    public string? BaseUrl { get; set; }
}

public sealed record AiStatus(bool Enabled, string Provider, string Model, string? Problem);

public sealed class ProposalException(string message) : Exception(message);

// One structured question to the AI: instructions, the question and the JSON schema of the answer, which parse reads.
public interface IPipelineProposer
{
    AiStatus Status { get; }

    Task<T> AskAsync<T>(string system, string user, JsonObject schema, Func<string, T> parse, CancellationToken cancellationToken);
}

public static class PipelineProposerExtensions
{
    public static Task<PipelineDraft> ProposeAsync(this IPipelineProposer proposer, string description, PipelineSpec? current, CancellationToken cancellationToken) =>
        proposer.AskAsync(ProposalPrompt.System(), ProposalPrompt.User(description, current), ProposalPrompt.Schema(), ProposalPrompt.Parse, cancellationToken);
}

public sealed class DisabledPipelineProposer(string provider, string reason) : IPipelineProposer
{
    public AiStatus Status { get; } = new(false, provider, "", reason);

    public Task<T> AskAsync<T>(string system, string user, JsonObject schema, Func<string, T> parse, CancellationToken cancellationToken) =>
        throw new ProposalException(Status.Problem!);
}
