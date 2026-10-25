using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;

namespace SmoothLlmImposter.Infrastructure.Clients.Optimizer;

internal sealed class OptimizerClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LlmMessagesOptions> options) : IOptimizerClient
{
    internal const string HttpClientName = "llm-messages";

    public async Task<string> OptimizeAsync(string systemInstructions, string userText, CancellationToken cancellationToken)
    {
        LlmMessagesOptions settings = options.Value;

        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemInstructions },
                new JsonObject { ["role"] = "user", ["content"] = userText },
            },
            ["stream"] = false,
        };

        using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 60);

        int attempts = Math.Max(1, settings.MaxAttempts);
        Exception? last = null;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + settings.Path)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
                }

                using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (JsonNode.Parse(responseBody) is not JsonObject envelope ||
                    envelope["choices"] is not JsonArray choices ||
                    choices.Count == 0 ||
                    choices[0] is not JsonObject choice ||
                    choice["message"] is not JsonObject messageObj)
                {
                    throw new JsonException("Optimizer returned an invalid Chat Completions envelope.");
                }

                return MessageContentText(messageObj["content"]);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout expiry without caller cancellation: retryable.
                last = ex;
                if (attempt < attempts - 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);
                }
            }
            catch (HttpRequestException ex) when (IsRetryableStatus(ex))
            {
                last = ex;
                if (attempt < attempts - 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);
                }
            }
            catch (HttpRequestException ex)
            {
                // Permanent failure (e.g. 4xx): do not retry.
                last = ex;
                break;
            }
            catch (JsonException)
            {
                // Deterministic envelope/content failure: do not retry.
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < attempts - 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);
                }
            }
        }

        throw last ?? new InvalidOperationException("Optimizer request failed.");
    }

    private static bool IsRetryableStatus(HttpRequestException ex) =>
        ex.StatusCode is null ||
        (int)ex.StatusCode >= 500 ||
        ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests;

    private static string MessageContentText(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            if (string.IsNullOrEmpty(text))
            {
                throw new JsonException("Empty optimizer content string.");
            }
            return text;
        }

        throw new JsonException("Unsupported content shape in optimizer reply.");
    }
}
