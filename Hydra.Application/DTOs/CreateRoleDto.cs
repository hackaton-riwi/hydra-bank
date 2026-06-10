namespace Hydra.Application.DTOs;

using System.ComponentModel.DataAnnotations;

public class CreateRoleDto
{
    [Required]
    public string Name { get; set; } = string.Empty;
}
