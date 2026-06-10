using System.Security.Claims;
using Hydra.Application.Caching;
using Hydra.Application.DTOs;
using Hydra.Domain.Entities;
using Hydra.Infrastructure.DATA;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/v1/exchange-rates")]
[Authorize(Roles = "ADMIN")]
public class ExchangeRatesController : ControllerBase
{
    private readonly BankOsDbContext _dbContext;
    private readonly IDistributedCache _cache;

    public ExchangeRatesController(BankOsDbContext dbContext, IDistributedCache cache)
    {
        _dbContext = dbContext;
        _cache = cache;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var tenantId = GetTenantId();
        var rates = await _dbContext.ExchangeRates
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.FromCurrency)
            .ThenBy(x => x.ToCurrency)
            .Select(x => new
            {
                x.Id,
                x.FromCurrency,
                x.ToCurrency,
                x.Rate,
                x.CreatedAt
            })
            .ToListAsync();

        return Ok(Success("EXCHANGE_RATES", "Tasas consultadas correctamente", rates));
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertExchangeRateDto request)
    {
        var tenantId = GetTenantId();
        var fromCurrency = NormalizeCurrency(request.FromCurrency);
        var toCurrency = NormalizeCurrency(request.ToCurrency);

        if (fromCurrency == toCurrency)
            return BadRequest(Error("INVALID_CURRENCY_PAIR", "La moneda origen y destino deben ser diferentes"));

        var rate = await _dbContext.ExchangeRates
            .SingleOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.FromCurrency == fromCurrency &&
                x.ToCurrency == toCurrency);

        if (rate is null)
        {
            rate = new ExchangeRate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                FromCurrency = fromCurrency,
                ToCurrency = toCurrency,
                Rate = request.Rate,
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.ExchangeRates.Add(rate);
        }
        else
        {
            rate.Rate = request.Rate;
        }

        await _dbContext.SaveChangesAsync();
        await _cache.RemoveAsync(BankCacheKeys.ExchangeRate(tenantId, fromCurrency, toCurrency));

        return Ok(Success("EXCHANGE_RATE_SAVED", "Tasa guardada correctamente", new
        {
            rate.Id,
            rate.FromCurrency,
            rate.ToCurrency,
            rate.Rate,
            rate.CreatedAt
        }));
    }

    private Guid GetTenantId()
    {
        var tenantIdClaim = User.FindFirst("tenant_id")?.Value;

        if (!Guid.TryParse(tenantIdClaim, out var tenantId))
            throw new UnauthorizedAccessException("El token no contiene tenant_id válido");

        return tenantId;
    }

    private static string NormalizeCurrency(string currency)
    {
        return currency.Trim().ToUpperInvariant();
    }

    private static object Success(string code, string description, object data)
    {
        return new
        {
            success = true,
            code,
            description,
            data
        };
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
