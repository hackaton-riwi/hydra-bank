using System;
using System.Threading.Tasks;
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

    [HttpDelete("{accountKey}")]
    public async Task<IActionResult> Deactivate(string accountKey)
    {
        try
        {
            return Ok(await _accountService.DeactivateAsync(accountKey));
        }
        catch (Exception exception)
        {
            return BadRequest(Error("ACCOUNT_DEACTIVATE_FAILED", exception.Message));
        }
    }

    [HttpPost("recharge")]
    public async Task<IActionResult> Recharge([FromBody] RechargeAccountDto request)
    {
        try
        {
            return Ok(await _accountService.RechargeAsync(request));
        }
        catch (Exception exception)
        {
            return BadRequest(Error("ACCOUNT_RECHARGE_FAILED", exception.Message));
        }
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMyAccount()
    {
        try
        {
            return Ok(await _accountService.GetMyAccountAsync());
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(Error("UNAUTHORIZED", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(Error("ACCOUNT_NOT_FOUND", ex.Message));
        }
    }

    [HttpGet("transactions")]
    public async Task<IActionResult> Transactions([FromQuery] TransactionHistoryQueryDto query)
    {
        return Ok(await _accountService.GetTransactionsAsync(query));
    }

    [HttpPost("transfer")]
    public async Task<IActionResult> Transfer([FromBody] TransferRequestDto dto)
    {
        try
        {
            var result = await _accountService.TransferAsync(dto);
            Response.Headers["X-Correlation-ID"] = _accountService.GetLastCorrelationId();
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Idempotency"))
        {
            return StatusCode(423, Error("IDEMPOTENCY_CONFLICT", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(Error("TRANSFER_FAILED", ex.Message));
        }
    }

    [HttpPost("deposit")]
    public async Task<IActionResult> Deposit([FromBody] DepositRequestDto dto)
    {
        try
        {
            var result = await _accountService.DepositAsync(dto);
            Response.Headers["X-Correlation-ID"] = _accountService.GetLastCorrelationId();
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Idempotency"))
        {
            return StatusCode(423, Error("IDEMPOTENCY_CONFLICT", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(Error("DEPOSIT_FAILED", ex.Message));
        }
    }

    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw([FromBody] WithdrawRequestDto dto)
    {
        try
        {
            var result = await _accountService.WithdrawAsync(dto);
            Response.Headers["X-Correlation-ID"] = _accountService.GetLastCorrelationId();
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Idempotency"))
        {
            return StatusCode(423, Error("IDEMPOTENCY_CONFLICT", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(Error("WITHDRAW_FAILED", ex.Message));
        }
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
