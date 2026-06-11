using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Hydra.Application.DTOs;

/// <summary>
/// Request for withdrawing funds from an account
/// </summary>
public class WithdrawRequestDto
{
    /// <summary>
    /// The source account ID or short ID, for example ACC-1234ABCD
    /// </summary>
    [Required]
    public string SourceAccountId { get; set; } = string.Empty;

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
    public string Id => TransactionShortId;

    [JsonIgnore]
    public Guid TransactionId { get; set; }

    public string TransactionShortId { get; set; } = string.Empty;

    public string SourceAccountShortId { get; set; } = string.Empty;

    [JsonIgnore]
    public Guid SourceAccountInternalId { get; set; }

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

    public decimal TotalDebit { get; set; }

    public decimal SourceBalance { get; set; }

    /// <summary>
    /// The timestamp when the transaction was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
