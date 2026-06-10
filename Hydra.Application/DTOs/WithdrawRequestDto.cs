using System.ComponentModel.DataAnnotations;

namespace Hydra.Application.DTOs;

/// <summary>
/// Request for withdrawing funds from an account
/// </summary>
public class WithdrawRequestDto
{
    /// <summary>
    /// The source account ID (from which funds are withdrawn)
    /// </summary>
    [Required]
    public Guid SourceAccountId { get; set; }

    /// <summary>
    /// The amount to withdraw
    /// </summary>
    [Required]
    [Range(0.01, double.MaxValue)]
    public decimal Amount { get; set; }
}

/// <summary>
/// Response for withdrawing funds from an account
/// </summary>
public class WithdrawResponseDto
{
    /// <summary>
    /// The transaction ID
    /// </summary>
    public Guid TransactionId { get; set; }

    /// <summary>
    /// The status of the transaction
    /// </>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The original amount withdrawn
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