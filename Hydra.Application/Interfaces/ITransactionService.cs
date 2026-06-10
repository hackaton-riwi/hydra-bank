using Hydra.Application.DTOs;

namespace Hydra.Application.Interfaces;

public interface ITransactionService
{
    Task<TransferResponseDto> TransferAsync(
        Guid tenantId, Guid userId, TransferRequestDto request,
        string idempotencyKey, string correlationId);
}
