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

    [HttpGet("transactions")]
    public async Task<IActionResult> Transactions([FromQuery] TransactionHistoryQueryDto query)
    {
        return Ok(await _accountService.GetTransactionsAsync(query));
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
