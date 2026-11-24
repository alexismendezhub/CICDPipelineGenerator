using CICDPipelineGenerator.Application.Ai;
using CICDPipelineGenerator.Application.Import;
using CICDPipelineGenerator.Infrastructure.Import;
using CICDPipelineGenerator.Infrastructure.Ai;

namespace CICDPipelineGenerator.Infrastructure;

public static class DependencyInjection
{
    /// <summary>The AI proposer chosen by Ai:Provider (Anthropic or None).</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var ai = configuration.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
        services.AddSingleton<IPipelineProposer>(provider => ai.Provider switch
        {
            "Anthropic" => new AnthropicPipelineProposer(ai, provider.GetRequiredService<ILogger<AnthropicPipelineProposer>>()),
            "None" => new DisabledPipelineProposer("None", "The AI is turned off (Ai:Provider is None)."),
            _ => new DisabledPipelineProposer(ai.Provider, $"Ai:Provider \"{ai.Provider}\" is not known: use Anthropic or None."),
        });
        services.AddSingleton<IPipelineImporter, YamlPipelineImporter>();
        return services;
    }
}
