using System.ComponentModel.DataAnnotations;

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
    /// <summary>
    /// The transaction ID
    /// </summary>
    public Guid TransactionId { get; set; }

    /// <summary>
    /// The status of the transaction
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The source account ID
    /// </summary>
    public Guid SourceAccountId { get; set; }

    /// <summary>
    /// The destination account ID
    /// </summary>
    public Guid DestinationAccountId { get; set; }

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
