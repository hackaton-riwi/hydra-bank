using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Hydra.Application.DTOs;

/// <summary>
/// Request for depositing funds into an account
/// </summary>
public class DepositRequestDto
{
    /// <summary>
    /// The destination account ID or short ID, for example ACC-1234ABCD
    /// </summary>
    [Required]
    public string DestinationAccountId { get; set; } = string.Empty;

    /// <summary>
    /// The amount to deposit
    /// </summary>
    [Required]
    [Range(0.01, double.MaxValue)]
    public decimal Amount { get; set; }
}

/// <summary>
/// Response for depositing funds into an account
/// </summary>
public class DepositResponseDto
{
    public string Id => TransactionShortId;

    [JsonIgnore]
    public Guid TransactionId { get; set; }

    public string TransactionShortId { get; set; } = string.Empty;

    public string DestinationAccountShortId { get; set; } = string.Empty;

    [JsonIgnore]
    public Guid DestinationAccountInternalId { get; set; }

    /// <summary>
    /// The status of the transaction
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The original amount deposited
    /// </summary>
    public decimal OriginalAmount { get; set; }

    /// <summary>
    /// The fee amount applied
    /// </summary>
    public decimal FeeAmount { get; set; }

    public decimal NetAmount { get; set; }

    public decimal DestinationBalance { get; set; }

    /// <summary>
    /// The timestamp when the transaction was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
