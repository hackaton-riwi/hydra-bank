using System.Text.RegularExpressions;
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
[Route("api/[controller]")]
[Authorize(Roles = "SUPERADMIN")]
[EnableRateLimiting("financial")]
public class TenantsController : ControllerBase
{
    private static readonly Regex SlugRegex = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
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

        var adminEmail = request.AdminEmail.Trim().ToLowerInvariant();

        if (await _dbContext.BankUsers.AnyAsync(user => user.Email.ToLower() == adminEmail))
        {
            return Conflict(new
            {
                message = "Ya existe un usuario bancario con ese correo"
            });
        }

        if (await _userManager.FindByEmailAsync(adminEmail) is not null)
        {
            return Conflict(new
            {
                message = "Ya existe un usuario de autenticación con ese correo"
            });
        }

        if (!await _roleManager.RoleExistsAsync(TenantAdminRole))
        {
            await _roleManager.CreateAsync(new IdentityRole(TenantAdminRole));
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

        var bankAdmin = new BankUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            FullName = request.AdminFullName.Trim(),
            Email = adminEmail,
            Role = UserRole.ADMIN,
            CreatedAt = now,
            UpdatedAt = now
        };
        bankAdmin.PasswordHash = _passwordHasher.HashPassword(bankAdmin, request.AdminPassword);

        var identityAdmin = new IdentityUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            EmailConfirmed = true
        };

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        _dbContext.Tenants.Add(tenant);
        _dbContext.BankUsers.Add(bankAdmin);
        await _dbContext.SaveChangesAsync();

        var identityResult = await _userManager.CreateAsync(identityAdmin, request.AdminPassword);

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
            },
            admin = new
            {
                bankAdmin.Id,
                bankAdmin.TenantId,
                bankAdmin.FullName,
                bankAdmin.Email,
                Role = bankAdmin.Role.ToString(),
                bankAdmin.CreatedAt
            }
        });
    }
}
