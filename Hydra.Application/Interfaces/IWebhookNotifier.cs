using System.Text.Json;

namespace Hydra.Application.Interfaces;

/// <summary>
/// Service for sending webhook notifications asynchronously
/// </summary>
public interface IWebhookNotifier
{
    /// <summary>
    /// Sends a webhook notification asynchronously (fire and forget)
    /// </summary>
    /// <param name="webhookUrl">The webhook URL to notify</param>
    /// <param name="payload">The payload to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task NotifyAsync(string webhookUrl, object payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Standard webhook payload for transaction events
/// </summary>
public class WebhookTransactionPayload
{
    public string Event { get; set; } = "TRANSACTION_COMPLETED";
    public Guid TransactionId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal FeeAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public Guid? SourceAccountId { get; set; }
    public Guid? DestinationAccountId { get; set; }
    public string? CorrelationId { get; set; }
}