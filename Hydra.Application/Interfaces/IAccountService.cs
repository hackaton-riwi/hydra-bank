using Hydra.Application.DTOs;

namespace Hydra.Application.Interfaces;

public interface IAccountService
{
    Task<object> CreateAsync(CreateAccountDto request);
}