using System.Text.RegularExpressions;
using Hydra.Application.DTOs;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Infrastructure.DATA;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "ADMIN")]
public class TenantsController : ControllerBase
{
    private static readonly Regex SlugRegex = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    private readonly BankOsDbContext _dbContext;

    public TenantsController(BankOsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateTenantDto request)
    {
        var slug = request.Slug.Trim().ToLowerInvariant();
        var mainCurrency = request.MainCurrency.Trim().ToUpperInvariant();

        if (!SlugRegex.IsMatch(slug))
        {
            return BadRequest(new
            {
                message = "El slug solo permite letras minúsculas, números y guiones. Ejemplo: mi-banco"
            });
        }

        if (!Enum.TryParse<FeeTypeEnum>(request.FeeType.Trim(), ignoreCase: true, out var feeType))
        {
            return BadRequest(new
            {
                message = "FeeType debe ser FIXED o PERCENTAGE"
            });
        }

        if (feeType == FeeTypeEnum.PERCENTAGE && request.FeeValue > 100)
        {
            return BadRequest(new
            {
                message = "FeeValue no puede ser mayor a 100 cuando FeeType es PERCENTAGE"
            });
        }

        if (await _dbContext.Tenants.AnyAsync(tenant => tenant.Slug == slug))
        {
            return Conflict(new
            {
                message = "Ya existe un tenant con ese slug"
            });
        }

        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Slug = slug,
            MainCurrency = mainCurrency,
            MaxTransactionAmount = request.MaxTransactionAmount,
            FeeType = feeType,
            FeeValue = request.FeeValue,
            WebhookUrl = string.IsNullOrWhiteSpace(request.WebhookUrl) ? null : request.WebhookUrl.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Tenants.Add(tenant);
        await _dbContext.SaveChangesAsync();

        return Created($"/api/tenants/{tenant.Id}", new
        {
            tenant = new
            {
                tenant.Id,
                tenant.Name,
                tenant.Slug,
                tenant.MainCurrency,
                tenant.MaxTransactionAmount,
                FeeType = tenant.FeeType.ToString(),
                tenant.FeeValue,
                tenant.WebhookUrl,
                tenant.CreatedAt,
                tenant.UpdatedAt
            }
        });
    }
}
