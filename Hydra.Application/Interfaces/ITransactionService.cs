using Hydra.Application.DTOs;

namespace Hydra.Application.Interfaces;

public interface ITransactionService
{
    Task<TransferResponseDto> TransferAsync(
        Guid tenantId, Guid userId, TransferRequestDto request,
        string idempotencyKey, string correlationId);

    Task<DepositResponseDto> DepositAsync(
        Guid tenantId, Guid userId, DepositRequestDto request,
        string idempotencyKey, string correlationId);

    Task<WithdrawResponseDto> WithdrawAsync(
        Guid tenantId, Guid userId, WithdrawRequestDto request,
        string idempotencyKey, string correlationId);
}
