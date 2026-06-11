using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Hydra.Application.DTOs;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Infrastructure.DATA;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using BankUser = Hydra.Domain.Entities.User;

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/v1/tenants")]
[EnableRateLimiting("financial")]
public class TenantsController : ControllerBase
{
    private static readonly Regex SlugRegex = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex InvalidSlugCharactersRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private const string DefaultMainCurrency = "COP";
    private const decimal DefaultMaxTransactionAmount = 5_000_000m;
    private const decimal DefaultFeeValue = 0m;
    private const string TenantAdminRole = "ADMIN";

    private readonly BankOsDbContext _dbContext;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IPasswordHasher<BankUser> _passwordHasher;

    public TenantsController(
        BankOsDbContext dbContext,
        UserManager<IdentityUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IPasswordHasher<BankUser> passwordHasher)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _roleManager = roleManager;
        _passwordHasher = passwordHasher;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> List()
    {
        var tenants = await _dbContext.Tenants
            .AsNoTracking()
            .OrderBy(tenant => tenant.Name)
            .Select(tenant => new
            {
                id = BuildShortId("TEN", tenant.Id),
                tenant.Name,
                tenant.Slug,
                tenant.MainCurrency,
                tenant.MaxTransactionAmount,
                FeeType = tenant.FeeType.ToString(),
                tenant.FeeValue,
                tenant.CreatedAt
            })
            .ToListAsync();

        return Ok(new
        {
            tenants
        });
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Create(CreateTenantDto request)
    {
        var tenantName = request.NombreTenant.Trim();
        var slug = BuildSlug(tenantName);

        if (!SlugRegex.IsMatch(slug))
        {
            return BadRequest(new
            {
                message = "El slug solo permite letras minúsculas, números y guiones. Ejemplo: mi-banco"
            });
        }

        if (await _dbContext.Tenants.AnyAsync(tenant => tenant.Slug == slug))
        {
            var slugPrefix = slug.Length > 41 ? slug[..41].Trim('-') : slug;
            slug = $"{slugPrefix}-{Guid.NewGuid():N}"[..50].Trim('-');
        }

        var adminEmail = request.Correo.Trim().ToLowerInvariant();

        var identityUserName = BuildTenantIdentityUserName(slug, adminEmail);

        if (await _userManager.FindByNameAsync(identityUserName) is not null)
        {
            return Conflict(new
            {
                message = "Ya existe un usuario de autenticación con ese correo"
            });
        }

        var adminPassword = request.Password;

        if (!await _roleManager.RoleExistsAsync(TenantAdminRole))
        {
            await _roleManager.CreateAsync(new IdentityRole(TenantAdminRole));
        }

        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = tenantName,
            Slug = slug,
            MainCurrency = DefaultMainCurrency,
            MaxTransactionAmount = DefaultMaxTransactionAmount,
            FeeType = FeeTypeEnum.FIXED,
            FeeValue = DefaultFeeValue,
            WebhookUrl = null,
            CreatedAt = now,
            UpdatedAt = now
        };

        var bankAdmin = new BankUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            FullName = $"Administrador {tenantName}",
            DocumentNumber = $"ADMIN-{slug.ToUpperInvariant()[..Math.Min(slug.Length, 20)]}",
            Email = adminEmail,
            Role = UserRole.ADMIN,
            CreatedAt = now,
            UpdatedAt = now
        };
        bankAdmin.PasswordHash = _passwordHasher.HashPassword(bankAdmin, adminPassword);

        var identityAdmin = new IdentityUser
        {
            UserName = identityUserName,
            Email = adminEmail,
            EmailConfirmed = true
        };

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        _dbContext.Tenants.Add(tenant);
        _dbContext.BankUsers.Add(bankAdmin);
        _dbContext.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserId = bankAdmin.Id,
            Action = "TENANT_CREATED",
            OldValue = null,
            NewValue = JsonSerializer.Serialize(new
            {
                TenantId = tenant.Id,
                TenantShortId = BuildShortId("TEN", tenant.Id),
                tenant.Name,
                tenant.Slug,
                AdminUserId = bankAdmin.Id,
                AdminShortId = BuildShortId("USR", bankAdmin.Id),
                AdminEmail = bankAdmin.Email
            }),
            CreatedAt = now
        });
        await _dbContext.SaveChangesAsync();

        var identityResult = await _userManager.CreateAsync(identityAdmin, adminPassword);

        if (!identityResult.Succeeded)
        {
            await transaction.RollbackAsync();
            return BadRequest(identityResult.Errors);
        }

        var roleResult = await _userManager.AddToRoleAsync(identityAdmin, TenantAdminRole);

        if (!roleResult.Succeeded)
        {
            await transaction.RollbackAsync();
            await _userManager.DeleteAsync(identityAdmin);
            return BadRequest(roleResult.Errors);
        }

        await transaction.CommitAsync();

        return Created($"/api/v1/tenants/{tenant.Id}", new
        {
            tenant = new
            {
                id = BuildShortId("TEN", tenant.Id),
                tenant.Name,
                tenant.Slug,
                tenant.MainCurrency,
                tenant.MaxTransactionAmount,
                FeeType = tenant.FeeType.ToString(),
                tenant.FeeValue,
                tenant.WebhookUrl,
                tenant.CreatedAt,
                tenant.UpdatedAt
            },
            admin = new
            {
                id = BuildShortId("USR", bankAdmin.Id),
                tenantId = BuildShortId("TEN", bankAdmin.TenantId),
                bankAdmin.FullName,
                bankAdmin.Email,
                Role = bankAdmin.Role.ToString(),
                bankAdmin.CreatedAt
            }
        });
    }

    [HttpGet("{tenantKey}/users")]
    [Authorize(Roles = "ADMIN,SUPERADMIN")]
    public async Task<IActionResult> GetUsers(string tenantKey)
    {
        var tenant = await FindTenantAsync(tenantKey);
        if (tenant is null)
            return NotFound(Error("TENANT_NOT_FOUND", "Tenant no encontrado"));

        if (!CanAccessTenant(tenant.Id))
            return Forbid();

        var users = await _dbContext.BankUsers
            .AsNoTracking()
            .Where(user => user.TenantId == tenant.Id)
            .OrderBy(user => user.FullName)
            .Select(user => new
            {
                id = BuildShortId("USR", user.Id),
                tenantId = BuildShortId("TEN", user.TenantId),
                user.FullName,
                user.DocumentNumber,
                user.Email,
                Role = user.Role.ToString(),
                user.CreatedAt,
                Accounts = user.Accounts.Select(account => new
                {
                    id = BuildShortId("ACC", account.Id),
                    account.AccountNumber,
                    account.Balance,
                    account.Currency,
                    Status = account.Status.ToString(),
                    account.CreatedAt
                })
            })
            .ToListAsync();

        var totalBalance = await _dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.TenantId == tenant.Id)
            .SumAsync(account => account.Balance);

        return Ok(new
        {
            tenant = BuildTenantSummary(tenant),
            totalUsers = users.Count,
            totalBalance,
            users
        });
    }

    [HttpGet("{tenantKey}/transactions")]
    [Authorize(Roles = "ADMIN,SUPERADMIN")]
    public async Task<IActionResult> GetTransactionHistory(
        string tenantKey,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] TransactionType? type,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var tenant = await FindTenantAsync(tenantKey);
        if (tenant is null)
            return NotFound(Error("TENANT_NOT_FOUND", "Tenant no encontrado"));

        if (!CanAccessTenant(tenant.Id))
            return Forbid();

        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(offset, 0);

        var query = _dbContext.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.TenantId == tenant.Id);

        if (from.HasValue)
            query = query.Where(transaction => transaction.CreatedAt >= from.Value);

        if (to.HasValue)
            query = query.Where(transaction => transaction.CreatedAt <= to.Value);

        if (type.HasValue)
            query = query.Where(transaction => transaction.Type == type.Value);

        var total = await query.CountAsync();
        var totalMoved = await query
            .Where(transaction => transaction.Status == TransactionStatus.SUCCESS)
            .SumAsync(transaction => transaction.OriginalAmount);
        var totalFees = await query
            .Where(transaction => transaction.Status == TransactionStatus.SUCCESS)
            .SumAsync(transaction => transaction.FeeAmount ?? 0m);

        var items = await query
            .OrderByDescending(transaction => transaction.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(transaction => new
            {
                id = BuildShortId("TRX", transaction.Id),
                userId = BuildShortId("USR", transaction.UserId),
                UserName = transaction.User.FullName,
                UserDocument = transaction.User.DocumentNumber,
                Type = transaction.Type.ToString(),
                transaction.OriginalAmount,
                transaction.FeeAmount,
                sourceAccountId = transaction.SourceAccountId == null ? null : BuildShortId("ACC", transaction.SourceAccountId.Value),
                destinationAccountId = transaction.DestinationAccountId == null ? null : BuildShortId("ACC", transaction.DestinationAccountId.Value),
                Status = transaction.Status.ToString(),
                transaction.CorrelationId,
                transaction.CreatedAt
            })
            .ToListAsync();

        return Ok(new
        {
            tenant = BuildTenantSummary(tenant),
            limit,
            offset,
            total,
            totalMoved,
            totalFees,
            items
        });
    }

    [HttpGet("{tenantKey}/audit-logs")]
    [Authorize(Roles = "ADMIN,SUPERADMIN")]
    public async Task<IActionResult> GetAuditLogs(
        string tenantKey,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var tenant = await FindTenantAsync(tenantKey);
        if (tenant is null)
            return NotFound(Error("TENANT_NOT_FOUND", "Tenant no encontrado"));

        if (!CanAccessTenant(tenant.Id))
            return Forbid();

        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(offset, 0);

        var query = _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log => log.TenantId == tenant.Id);

        var total = await query.CountAsync();
        var logs = await query
            .OrderByDescending(log => log.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(log => new
            {
                id = BuildShortId("LOG", log.Id),
                userId = BuildShortId("USR", log.UserId),
                UserName = log.User.FullName,
                log.Action,
                log.OldValue,
                log.NewValue,
                log.CreatedAt
            })
            .ToListAsync();

        return Ok(new
        {
            tenant = BuildTenantSummary(tenant),
            limit,
            offset,
            total,
            logs
        });
    }

    [HttpGet("current/users")]
    [Authorize(Roles = "ADMIN,SUPERADMIN")]
    public Task<IActionResult> GetCurrentTenantUsers()
    {
        var tenantId = User.FindFirst("tenant_id")?.Value;
        return string.IsNullOrWhiteSpace(tenantId)
            ? Task.FromResult<IActionResult>(Unauthorized(Error("TENANT_REQUIRED", "El token no contiene tenant_id")))
            : GetUsers(tenantId);
    }

    [HttpGet("current/transactions")]
    [Authorize(Roles = "ADMIN,SUPERADMIN")]
    public Task<IActionResult> GetCurrentTenantTransactionHistory(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] TransactionType? type,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var tenantId = User.FindFirst("tenant_id")?.Value;
        return string.IsNullOrWhiteSpace(tenantId)
            ? Task.FromResult<IActionResult>(Unauthorized(Error("TENANT_REQUIRED", "El token no contiene tenant_id")))
            : GetTransactionHistory(tenantId, from, to, type, limit, offset);
    }

    [HttpGet("current/audit-logs")]
    [Authorize(Roles = "ADMIN,SUPERADMIN")]
    public Task<IActionResult> GetCurrentTenantAuditLogs(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var tenantId = User.FindFirst("tenant_id")?.Value;
        return string.IsNullOrWhiteSpace(tenantId)
            ? Task.FromResult<IActionResult>(Unauthorized(Error("TENANT_REQUIRED", "El token no contiene tenant_id")))
            : GetAuditLogs(tenantId, limit, offset);
    }

    [HttpDelete("{tenantKey}")]
    [Authorize(Roles = "SUPERADMIN")]
    public async Task<IActionResult> Delete(string tenantKey)
    {
        var tenant = await FindTenantAsync(tenantKey);
        if (tenant is null)
            return NotFound(Error("TENANT_NOT_FOUND", "Tenant no encontrado"));

        var bankUsers = await _dbContext.BankUsers
            .Where(user => user.TenantId == tenant.Id)
            .ToListAsync();
        var identityUserNames = bankUsers
            .Select(user => BuildTenantIdentityUserName(tenant.Slug, user.Email.Trim().ToLowerInvariant()))
            .ToList();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        var idempotencyRecords = await _dbContext.IdempotencyRecords
            .Where(record => record.TenantId == tenant.Id)
            .ToListAsync();
        var auditLogs = await _dbContext.AuditLogs
            .Where(log => log.TenantId == tenant.Id)
            .ToListAsync();
        var transactions = await _dbContext.Transactions
            .Where(item => item.TenantId == tenant.Id)
            .ToListAsync();
        var accounts = await _dbContext.Accounts
            .Where(account => account.TenantId == tenant.Id)
            .ToListAsync();

        _dbContext.IdempotencyRecords.RemoveRange(idempotencyRecords);
        _dbContext.AuditLogs.RemoveRange(auditLogs);
        _dbContext.Transactions.RemoveRange(transactions);
        _dbContext.Accounts.RemoveRange(accounts);
        _dbContext.BankUsers.RemoveRange(bankUsers);
        _dbContext.Tenants.Remove(tenant);
        await _dbContext.SaveChangesAsync();

        foreach (var identityUserName in identityUserNames)
        {
            var identityUser = await _userManager.FindByNameAsync(identityUserName);
            if (identityUser is not null)
            {
                await _userManager.DeleteAsync(identityUser);
            }
        }

        await transaction.CommitAsync();

        return Ok(new
        {
            success = true,
            code = "TENANT_DELETED",
            description = "Tenant eliminado con usuarios, cuentas, transacciones, logs e identidades de autenticación",
            tenant = BuildTenantSummary(tenant),
            deleted = new
            {
                users = bankUsers.Count,
                accounts = accounts.Count,
                transactions = transactions.Count,
                auditLogs = auditLogs.Count,
                idempotencyRecords = idempotencyRecords.Count
            }
        });
    }

    private static string BuildSlug(string tenantName)
    {
        var rawSlug = tenantName.Trim().ToLowerInvariant();
        var slug = InvalidSlugCharactersRegex.Replace(rawSlug, "-").Trim('-');

        return string.IsNullOrWhiteSpace(slug)
            ? $"tenant-{Guid.NewGuid():N}"[..50]
            : slug.Length <= 50 ? slug : slug[..50].Trim('-');
    }

    private static string BuildTenantIdentityUserName(string tenantSlug, string email)
    {
        return $"{tenantSlug}:{email}";
    }

    private async Task<Tenant?> FindTenantAsync(string tenantKey)
    {
        var normalized = tenantKey.Trim().ToLowerInvariant();

        if (Guid.TryParse(normalized, out var tenantId))
        {
            return await _dbContext.Tenants.SingleOrDefaultAsync(tenant => tenant.Id == tenantId);
        }

        var shortCode = normalized.ToUpperInvariant();
        const string prefix = "TEN-";
        if (shortCode.StartsWith(prefix, StringComparison.Ordinal))
        {
            shortCode = shortCode[prefix.Length..];
            var tenants = await _dbContext.Tenants
                .AsNoTracking()
                .ToListAsync();

            var matches = tenants
                .Where(tenant => tenant.Id.ToString("N").StartsWith(shortCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }

        return await _dbContext.Tenants.SingleOrDefaultAsync(tenant => tenant.Slug == normalized);
    }

    private bool CanAccessTenant(Guid tenantId)
    {
        if (User.IsInRole("SUPERADMIN"))
            return true;

        return Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var currentTenantId)
            && currentTenantId == tenantId;
    }

    private static object BuildTenantSummary(Tenant tenant)
    {
        return new
        {
            id = BuildShortId("TEN", tenant.Id),
            tenant.Name,
            tenant.Slug,
            tenant.MainCurrency,
            tenant.MaxTransactionAmount,
            FeeType = tenant.FeeType.ToString(),
            tenant.FeeValue
        };
    }

    private static string BuildShortId(string prefix, Guid id)
    {
        return $"{prefix}-{id.ToString("N")[..8].ToUpperInvariant()}";
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
