namespace Hydra.Application.DTOs;

using System.ComponentModel.DataAnnotations;

public class AssignRoleDto
{
    [Required]
    public string Role { get; set; } = string.Empty;
}
