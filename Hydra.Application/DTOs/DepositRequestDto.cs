using System.ComponentModel.DataAnnotations;

namespace Hydra.Application.DTOs;

/// <summary>
/// Request for depositing funds into an account
/// </summary>
public class DepositRequestDto
{
    /// <summary>
    /// The destination account ID (to which funds are deposited)
    /// </summary>
    [Required]
    public Guid DestinationAccountId { get; set; }

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
    /// <summary>
    /// The transaction ID
    /// </>
    public Guid TransactionId { get; set; }

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

    /// <summary>
    /// The timestamp when the transaction was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
}