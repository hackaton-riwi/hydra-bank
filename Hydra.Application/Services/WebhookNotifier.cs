using Hydra.Application.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hydra.Application.Services;

/// <summary>
/// HTTP-based webhook notifier with fire-and-forget semantics
/// </summary>
public class WebhookNotifier : IWebhookNotifier
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookNotifier> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public WebhookNotifier(HttpClient httpClient, ILogger<WebhookNotifier> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task NotifyAsync(string webhookUrl, object payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return;

        // Fire and forget - don't await in production, but catch exceptions
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
                {
                    Content = JsonContent.Create(payload, options: _jsonOptions)
                };
                request.Headers.Add("User-Agent", "HydraBank-Webhook/1.0");
                request.Headers.Add("X-Webhook-Source", "hydra-bank");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Webhook delivered successfully to {Url}", webhookUrl);
                }
                else
                {
                    _logger.LogWarning("Webhook failed with status {Status} to {Url}", 
                        response.StatusCode, webhookUrl);
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Webhook timeout to {Url}", webhookUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook error to {Url}", webhookUrl);
            }
        }, cancellationToken);

        await Task.CompletedTask; // Keep method signature async
    }
}