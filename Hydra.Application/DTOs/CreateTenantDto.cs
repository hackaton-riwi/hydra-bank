namespace Hydra.Application.DTOs;

using System.ComponentModel.DataAnnotations;

public class CreateTenantDto
{
    [Required]
    [MaxLength(100)]
    public string NombreTenant { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(150)]
    public string Correo { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string Password { get; set; } = string.Empty;
}
