using System.Text.Json.Serialization;
using SmoothLlmImposter.Application.Features.AuthorizationOverride;
using SmoothLlmImposter.Application.Features.Credentials;
using SmoothLlmImposter.Application.Features.ProviderConfiguration;
using SmoothLlmImposter.Host.Endpoints;

namespace SmoothLlmImposter.Host.Configuration;

/// <summary>
/// Source-generated JSON metadata for every DTO the minimal-API endpoints bind from a request
/// body or return as a typed result. NativeAOT ships no runtime reflection fallback, so the
/// RequestDelegateGenerator output resolves that metadata exclusively through this context
/// (wired in <c>Program.cs</c> via <c>ConfigureHttpJsonOptions</c>). Omitting an endpoint DTO
/// here crashes the AOT host at startup with <c>NotSupportedException</c> (JsonTypeInfo metadata
/// was not provided) before any request is served — the JIT host never notices.
/// </summary>
[JsonSerializable(typeof(CredentialAdminEndpoints.CreateCredentialBody))]
[JsonSerializable(typeof(CredentialAdminEndpoints.UpdateCredentialBody))]
[JsonSerializable(typeof(ProviderConfigurationBody))]
[JsonSerializable(typeof(CredentialResponse))]
[JsonSerializable(typeof(CredentialMutationResponse))]
[JsonSerializable(typeof(IReadOnlyList<CredentialResponse>))]
[JsonSerializable(typeof(ProviderConfigurationResponse))]
[JsonSerializable(typeof(IReadOnlyList<ProviderConfigurationResponse>))]
[JsonSerializable(typeof(AuthorizationOverrideState))]
[JsonSerializable(typeof(HealthResponse))]
internal partial class ImposterJsonContext : JsonSerializerContext;
