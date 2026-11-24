# Publishing CI/CD Pipeline Generator in the cloud: architecture, cost and open findings

Written from a read of the code (not from a deployed test).

## Findings

| # | Finding (from the code) | Risk in the cloud | Fix |
| --- | --- | --- | --- |
| 1 | No sign-in on `/api` (`Program.cs`). | Anyone can use `/api/ai/propose` with the Anthropic key. | Add sign-in (for example Microsoft Entra External ID). |
| 2 | `/api/ai/propose` calls Claude with `MaxTokens = 16000` and no per-user limit. | Unbounded AI bill. | Per-user quota, `AddRateLimiter`, a budget alert on the Anthropic account. |
| 3 | Listens on `http://localhost:5090` unless `ASPNETCORE_URLS` is set. | None once configured. | `ASPNETCORE_URLS=http://+:8080` in the container. |
| 4 | The Anthropic key comes from configuration or `ANTHROPIC_API_KEY`. | Key in plain configuration. | Key Vault reference through a managed identity. |
| 5 | Stateless otherwise (generation is pure .NET, no external tools). | Low. | Smallest container (0.5 vCPU / 1 GiB), can scale to zero. |

## Target architecture

One Azure Container Apps app (scale to zero) built from the Server project, an Azure Container Registry, a Key Vault
for the Anthropic key and Application Insights for logs.

## Cost

About $10 to $15 a month of compute, plus about $0.05 to $0.20 per AI call on Claude Opus 5.5 ($4 / $20 per million
tokens in and out; the token counts are an estimate to measure).
