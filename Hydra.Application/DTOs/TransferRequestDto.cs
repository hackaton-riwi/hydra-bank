using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Hydra.Application.DTOs;

/// <summary>
/// Request for transferring funds between accounts
/// </summary>
public class TransferRequestDto
{
    /// <summary>
    /// The registered destination client's document number
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string DestinationDocumentNumber { get; set; } = string.Empty;

    /// <summary>
    /// The amount to transfer in COP
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
    public string Id => TransactionShortId;

    [JsonIgnore]
    public Guid TransactionId { get; set; }

    public string TransactionShortId { get; set; } = string.Empty;

    /// <summary>
    /// The status of the transaction
    /// </summary>
    public string Status { get; set; } = string.Empty;

    [JsonIgnore]
    public Guid SourceAccountId { get; set; }

    public string SourceAccountShortId { get; set; } = string.Empty;

    [JsonIgnore]
    public Guid DestinationAccountId { get; set; }

    public string DestinationAccountShortId { get; set; } = string.Empty;

    /// <summary>
    /// The destination document number
    /// </summary>
    public string DestinationDocumentNumber { get; set; } = string.Empty;

    /// <summary>
    /// The amount transferred
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// The fee amount applied
    /// </summary>
    public decimal FeeAmount { get; set; }

    public decimal SourceBalance { get; set; }

    public decimal DestinationBalance { get; set; }

    /// <summary>
    /// The timestamp when the transaction was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
