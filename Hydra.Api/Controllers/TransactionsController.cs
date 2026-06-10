using Hydra.Application.DTOs;
using Hydra.Application.Interfaces;
using Hydra.Domain.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
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
    }

    [HttpPost("deposit")]
    public async Task<IActionResult> Deposit(DepositRequestDto dto)
    {
        try
        {
            var (tenantId, userId) = GetUserContext();
            var (idempotencyKey, correlationId) = GetRequestHeaders();
            var result = await _transactionService.DepositAsync(
                tenantId, userId, dto, idempotencyKey, correlationId);
            Response.Headers["X-Correlation-ID"] = correlationId;
            return Ok(result);
        }
        catch (TransactionInProgressException ex)
        {
            return StatusCode(423, new { message = ex.Message });
        }
    }

    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw(WithdrawRequestDto dto)
    {
        try
        {
            var (tenantId, userId) = GetUserContext();
            var (idempotencyKey, correlationId) = GetRequestHeaders();
            var result = await _transactionService.WithdrawAsync(
                tenantId, userId, dto, idempotencyKey, correlationId);
            Response.Headers["X-Correlation-ID"] = correlationId;
            return Ok(result);
        }
        catch (TransactionInProgressException ex)
        {
            return StatusCode(423, new { message = ex.Message });
        }
    }

    private (string idempotencyKey, string correlationId) GetRequestHeaders()
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault()
            ?? throw new BadHttpRequestException("Header Idempotency-Key requerido");

        var correlationId = Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        return (idempotencyKey, correlationId);
    }

    private (Guid tenantId, Guid userId) GetUserContext()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("Usuario no autenticado");

        var tenantClaim = User.FindFirst("tenant_id")?.Value;

        return (
            Guid.TryParse(tenantClaim, out var tid) ? tid : Guid.Empty,
            Guid.Parse(userId));
    }
}
