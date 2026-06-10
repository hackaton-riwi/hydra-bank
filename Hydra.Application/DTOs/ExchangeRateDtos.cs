using System.ComponentModel.DataAnnotations;

namespace Hydra.Application.DTOs;

public class UpsertExchangeRateDto
{
    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string FromCurrency { get; set; } = string.Empty;

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string ToCurrency { get; set; } = string.Empty;

    [Range(0.00000001, double.MaxValue)]
    public decimal Rate { get; set; }
}
