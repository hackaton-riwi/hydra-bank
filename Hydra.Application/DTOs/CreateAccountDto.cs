namespace Hydra.Application.DTOs;

public class CreateAccountDto
{
    public Guid OwnerId { get; set; }

    public string Currency { get; set; } = string.Empty;
}