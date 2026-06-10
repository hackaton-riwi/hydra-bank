using System.ComponentModel.DataAnnotations;

namespace Hydra.Application.DTOs;

public class RegisterTenantClientDto
{
    [Required]
    [MaxLength(50)]
    public string TenantSlug { get; set; } = string.Empty;

    [Required]
    [MaxLength(150)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string DocumentNumber { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(150)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string Password { get; set; } = string.Empty;
}
