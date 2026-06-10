using Hydra.Application.DTOs;

namespace Hydra.Application.Interfaces;

public interface IAccountService
{
    Task<object> CreateAsync(CreateAccountDto request);

    Task<object> DeactivateAsync(Guid accountId);

    Task<ServiceResponse> DepositAsync(Guid accountId, MoneyOperationDto request, Guid idempotencyKey);

    Task<ServiceResponse> WithdrawAsync(Guid accountId, MoneyOperationDto request, Guid idempotencyKey);

    Task<ServiceResponse> TransferAsync(TransferDto request, Guid idempotencyKey);

    Task<object> GetTransactionsAsync(TransactionHistoryQueryDto query);
}
