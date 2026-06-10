using Hydra.Application.DTOs;
using Hydra.Infrastructure.DATA;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/v1/accounts")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly BankOsDbContext _dbContext;

    public AccountsController(BankOsDbContext dbContext)
    {
        _dbContext = dbContext;
    }
}