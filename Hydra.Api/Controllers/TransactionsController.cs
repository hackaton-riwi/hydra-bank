using Hydra.Application.DTOs;
using Hydra.Application.Interfaces;
using Hydra.Domain.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/v1/transactions")]
[Authorize(Roles = "CLIENT")]
[EnableRateLimiting("financial")]
[ApiExplorerSettings(IgnoreApi = true)]
public class TransactionsController : ControllerBase
{
    private readonly ITransactionService _transactionService;

    public TransactionsController(ITransactionService transactionService)
    {
        _transactionService = transactionService;
    }

    [HttpPost("transfer")]
    public async Task<IActionResult> Transfer(TransferRequestDto dto)
    {
        try
        {
            var (tenantId, userId) = GetUserContext();
            var (idempotencyKey, correlationId) = GetRequestHeaders();
            var result = await _transactionService.TransferAsync(
                tenantId, userId, dto, idempotencyKey, correlationId);
            Response.Headers["X-Correlation-ID"] = correlationId;
            return Ok(result);
        }
        catch (TransactionInProgressException ex)
        {
            return StatusCode(423, new { message = ex.Message });
        }
        catch (BadHttpRequestException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private (string idempotencyKey, string correlationId) GetRequestHeaders()
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault()
            ?? throw new BadHttpRequestException("Header Idempotency-Key requerido");

        var correlationId = Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        if (!Guid.TryParse(idempotencyKey, out _))
            throw new BadHttpRequestException("Header Idempotency-Key debe ser un UUID");

        if (!Guid.TryParse(correlationId, out _))
            throw new BadHttpRequestException("Header X-Correlation-ID debe ser un UUID");

        return (idempotencyKey, correlationId);
    }

    private (Guid tenantId, Guid userId) GetUserContext()
    {
        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        var userClaim = User.FindFirst("user_id")?.Value;

        if (!Guid.TryParse(tenantClaim, out var tenantId) ||
            !Guid.TryParse(userClaim, out var userId))
        {
            throw new UnauthorizedAccessException("Token sin tenant_id o user_id validos");
        }

        return (tenantId, userId);
    }
}
