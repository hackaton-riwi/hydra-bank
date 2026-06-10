using Hydra.Application.DTOs;
using Hydra.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/v1/accounts")]
[Authorize(Roles = "CLIENT")]
[EnableRateLimiting("financial")]
public class AccountsController : ControllerBase
{
    private readonly IAccountService _accountService;

    public AccountsController(IAccountService accountService)
    {
        _accountService = accountService;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAccountDto request)
    {
        try
        {
            var account = await _accountService.CreateAsync(request);

            return Created("/api/v1/accounts", account);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Unauthorized(Error("UNAUTHORIZED", exception.Message));
        }
        catch (Exception exception)
        {
            return BadRequest(Error("ACCOUNT_CREATE_FAILED", exception.Message));
        }
    }

    [HttpDelete("{accountId:guid}")]
    public async Task<IActionResult> Deactivate(Guid accountId)
    {
        try
        {
            return Ok(await _accountService.DeactivateAsync(accountId));
        }
        catch (Exception exception)
        {
            return BadRequest(Error("ACCOUNT_DEACTIVATE_FAILED", exception.Message));
        }
    }

    [HttpPost("{accountId:guid}/deposit")]
    public async Task<IActionResult> Deposit(Guid accountId, [FromBody] MoneyOperationDto request)
    {
        if (!TryGetIdempotencyKey(out var idempotencyKey, out var error))
            return BadRequest(error);

        var response = await _accountService.DepositAsync(accountId, request, idempotencyKey);
        return StatusCode(response.StatusCode, response.Body);
    }

    [HttpPost("{accountId:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid accountId, [FromBody] MoneyOperationDto request)
    {
        if (!TryGetIdempotencyKey(out var idempotencyKey, out var error))
            return BadRequest(error);

        var response = await _accountService.WithdrawAsync(accountId, request, idempotencyKey);
        return StatusCode(response.StatusCode, response.Body);
    }

    [HttpPost("transfer")]
    public async Task<IActionResult> Transfer([FromBody] TransferDto request)
    {
        if (!TryGetIdempotencyKey(out var idempotencyKey, out var error))
            return BadRequest(error);

        var response = await _accountService.TransferAsync(request, idempotencyKey);
        return StatusCode(response.StatusCode, response.Body);
    }

    [HttpGet("transactions")]
    public async Task<IActionResult> Transactions([FromQuery] TransactionHistoryQueryDto query)
    {
        return Ok(await _accountService.GetTransactionsAsync(query));
    }

    private bool TryGetIdempotencyKey(out Guid idempotencyKey, out object? error)
    {
        idempotencyKey = Guid.Empty;
        error = null;

        if (!Guid.TryParse(Request.Headers["Idempotency-Key"].FirstOrDefault(), out idempotencyKey))
        {
            error = Error("IDEMPOTENCY_KEY_REQUIRED", "El header Idempotency-Key es obligatorio y debe ser un UUID");
            return false;
        }

        return true;
    }

    private static object Error(string code, string description)
    {
        return new
        {
            success = false,
            code,
            description
        };
    }
}
