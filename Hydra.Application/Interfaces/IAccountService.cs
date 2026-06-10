using Hydra.Application.DTOs;

namespace Hydra.Application.Interfaces;

public interface IAccountService
{
    Task<object> CreateAsync(CreateAccountDto request);

    Task<object> DeactivateAsync(Guid accountId);

    Task<object> RechargeAsync(RechargeAccountDto request);

    Task<object> GetTransactionsAsync(TransactionHistoryQueryDto query);
}
