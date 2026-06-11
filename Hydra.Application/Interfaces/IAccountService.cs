using Hydra.Application.DTOs;

namespace Hydra.Application.Interfaces;

public interface IAccountService
{
    Task<object> CreateAsync(CreateAccountDto request);

    Task<object> DeactivateAsync(string accountKey);

    Task<object> RechargeAsync(RechargeAccountDto request);

    Task<object> GetMyAccountAsync();

    Task<object> GetTransactionsAsync(TransactionHistoryQueryDto query);

    Task<TransferResponseDto> TransferAsync(TransferRequestDto dto);

    Task<DepositResponseDto> DepositAsync(DepositRequestDto dto);

    Task<WithdrawResponseDto> WithdrawAsync(WithdrawRequestDto dto);

    string GetLastCorrelationId();
}
