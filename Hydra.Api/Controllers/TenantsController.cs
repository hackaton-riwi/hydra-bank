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
[Route("api/v1/tenants")]
[EnableRateLimiting("financial")]
public class TenantsController : ControllerBase
{
    private static readonly Regex SlugRegex = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex InvalidSlugCharactersRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private const string DefaultMainCurrency = "USD";
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

        var adminPassword = BuildAdminPassword();

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
            Email = adminEmail,
            Role = UserRole.ADMIN,
            CreatedAt = now,
            UpdatedAt = now
        };
        bankAdmin.PasswordHash = _passwordHasher.HashPassword(bankAdmin, adminPassword);

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
                temporaryPassword = adminPassword,
                bankAdmin.CreatedAt
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

    private static string BuildAdminPassword()
    {
        return $"Admin{Guid.NewGuid():N}"[..12] + "a1";
    }
}
