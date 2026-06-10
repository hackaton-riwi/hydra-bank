using System.ComponentModel.DataAnnotations;

namespace Hydra.Application.DTOs;

/// <summary>
/// Request for transferring funds between accounts
/// </summary>
public class TransferRequestDto
{
    /// <summary>
    /// The source account ID (from which funds are withdrawn)
    /// </summary>
    [Required]
    public Guid SourceAccountId { get; set; }

    /// <summary>
    /// The destination account ID (to which funds are deposited)
    /// </summary>
    [Required]
    public Guid DestinationAccountId { get; set; }

    /// <summary>
    /// The amount to transfer (in source account currency)
    /// </summary>
    [Required]
    [Range(0.01, double.MaxValue)]
    public decimal Amount { get; set; }
}

/// <summary>
/// Response for transferring funds between accounts
/// </summary>
public class TransferResponseDto
{
    /// <summary>
    /// The transaction ID
    /// </summary>
    public Guid TransactionId { get; set; }

    /// <summary>
    /// The status of the transaction
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The original amount transferred
    /// </summary>
    public decimal OriginalAmount { get; set; }

    /// <summary>
    /// The fee amount applied
    /// </summary>
    public decimal FeeAmount { get; set; }

    /// <summary>
    /// The converted amount (in destination account currency)
    /// </summary>
    public decimal? ConvertedAmount { get; set; }

    /// <summary>
    /// The exchange rate used (if currency conversion occurred)
    /// </summary>
    public decimal? ExchangeRate { get; set; }

    /// <summary>
    /// The timestamp when the transaction was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
}