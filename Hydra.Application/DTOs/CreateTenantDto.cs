namespace Hydra.Application.DTOs;

using System.ComponentModel.DataAnnotations;

public class CreateTenantDto
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Slug { get; set; } = string.Empty;

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string MainCurrency { get; set; } = string.Empty;

    [Range(0.01, double.MaxValue)]
    public decimal MaxTransactionAmount { get; set; }

    [Required]
    public string FeeType { get; set; } = string.Empty;

    [Range(0, double.MaxValue)]
    public decimal FeeValue { get; set; }

    [Url]
    public string? WebhookUrl { get; set; }

    [Required]
    [MaxLength(150)]
    public string AdminFullName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(150)]
    public string AdminEmail { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string AdminPassword { get; set; } = string.Empty;
}
