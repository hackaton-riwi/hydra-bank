using Hydra.Application.DTOs;
using Hydra.Infrastructure.DATA;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BankUser = Hydra.Domain.Entities.User;
using Hydra.Domain.Enums;

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private const string SuperAdminRole = "SUPERADMIN";
    private const string AdminRole = "ADMIN";
    private const string ClientRole = "CLIENT";

    private readonly UserManager<IdentityUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly BankOsDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IPasswordHasher<BankUser> _passwordHasher;

    public AuthController(
        UserManager<IdentityUser> userManager,
        RoleManager<IdentityRole> roleManager,
        BankOsDbContext dbContext,
        IConfiguration configuration,
        IPasswordHasher<BankUser> passwordHasher)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _dbContext = dbContext;
        _configuration = configuration;
        _passwordHasher = passwordHasher;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(RegisterTenantClientDto request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var tenantSlug = request.TenantSlug.Trim().ToLowerInvariant();

        var tenant = await _dbContext.Tenants
            .SingleOrDefaultAsync(x => x.Slug == tenantSlug);

        if (tenant is null)
        {
            return NotFound(Error("TENANT_NOT_FOUND", "Tenant no encontrado"));
        }

        if (await _userManager.FindByEmailAsync(email) is not null ||
            await _dbContext.BankUsers.AnyAsync(x => x.TenantId == tenant.Id && x.Email.ToLower() == email))
        {
            return Conflict(Error("EMAIL_ALREADY_EXISTS", "Ya existe un usuario con ese correo"));
        }

        await EnsureDefaultRolesExist();

        var identityUser = new IdentityUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var now = DateTime.UtcNow;
        var bankUser = new BankUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            FullName = request.FullName.Trim(),
            Email = email,
            Role = UserRole.CLIENT,
            CreatedAt = now,
            UpdatedAt = now
        };
        bankUser.PasswordHash = _passwordHasher.HashPassword(bankUser, request.Password);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        _dbContext.BankUsers.Add(bankUser);
        await _dbContext.SaveChangesAsync();

        var identityResult = await _userManager.CreateAsync(identityUser, request.Password);

        if (!identityResult.Succeeded)
        {
            await transaction.RollbackAsync();
            return BadRequest(identityResult.Errors);
        }

        var roleResult = await _userManager.AddToRoleAsync(identityUser, ClientRole);

        if (!roleResult.Succeeded)
        {
            await transaction.RollbackAsync();
            await _userManager.DeleteAsync(identityUser);
            return BadRequest(roleResult.Errors);
        }

        await transaction.CommitAsync();

        var roles = await _userManager.GetRolesAsync(identityUser);
        var expiresAt = DateTime.UtcNow.AddMinutes(GetTokenExpirationMinutes());
        var token = GenerateJwtToken(identityUser, roles, bankUser, expiresAt);

        return Created("/api/v1/auth/register", BuildAuthResponse(token, expiresAt, identityUser, roles, bankUser));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginDto request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var tenantSlug = request.TenantSlug.Trim().ToLowerInvariant();

        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Slug == tenantSlug);

        if (tenant is null)
        {
            return Unauthorized(Error("INVALID_TENANT_CREDENTIALS", "Credenciales inválidas para el tenant"));
        }

        var bankUser = await _dbContext.BankUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.TenantId == tenant.Id &&
                x.Email.ToLower() == email);

        if (bankUser is null)
        {
            return Unauthorized(Error("INVALID_TENANT_CREDENTIALS", "Credenciales inválidas para el tenant"));
        }

        var user = await _userManager.FindByEmailAsync(email);

        if (user is null || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            return Unauthorized(Error("INVALID_TENANT_CREDENTIALS", "Credenciales inválidas para el tenant"));
        }

        var roles = await _userManager.GetRolesAsync(user);
        var expiresAt = DateTime.UtcNow.AddMinutes(GetTokenExpirationMinutes());
        var token = GenerateJwtToken(user, roles, bankUser, expiresAt);

        return Ok(BuildAuthResponse(token, expiresAt, user, roles, bankUser));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new
            {
                message = "Token inválido"
            });
        }

        var user = await _userManager.FindByIdAsync(userId);

        if (user is null)
        {
            return Unauthorized(new
            {
                message = "Usuario no encontrado"
            });
        }

        var roles = await _userManager.GetRolesAsync(user);
        var bankUser = await FindBankUserAsync(user);

        return Ok(new
        {
            user = BuildUserResponse(user, roles, bankUser)
        });
    }

    private async Task EnsureDefaultRolesExist()
    {
        foreach (var roleName in new[] { SuperAdminRole, AdminRole, ClientRole })
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                await _roleManager.CreateAsync(new IdentityRole(roleName));
            }
        }
    }

    private string GenerateJwtToken(
        IdentityUser user,
        IEnumerable<string> roles,
        Hydra.Domain.Entities.User? bankUser,
        DateTime expiresAt)
    {
        var jwtKey = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key no está configurado");
        var issuer = _configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException("Jwt:Issuer no está configurado");
        var audience = _configuration["Jwt:Audience"]
            ?? throw new InvalidOperationException("Jwt:Audience no está configurado");

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new("identity_user_id", user.Id)
        };

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        if (bankUser is not null)
        {
            claims.Add(new Claim("tenant_id", bankUser.TenantId.ToString()));
            claims.Add(new Claim("user_id", bankUser.Id.ToString()));
            claims.Add(new Claim("tenant_role", bankUser.Role.ToString()));
        }
        else
        {
            claims.Add(new Claim("user_id", user.Id));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private int GetTokenExpirationMinutes()
    {
        return int.TryParse(_configuration["Jwt:ExpireMinutes"], out var minutes)
            ? minutes
            : 60;
    }

    private static object BuildAuthResponse(
        string token,
        DateTime expiresAt,
        IdentityUser user,
        IEnumerable<string> roles,
        Hydra.Domain.Entities.User? bankUser)
    {
        return new
        {
            token,
            expiresAt,
            user = BuildUserResponse(user, roles, bankUser)
        };
    }

    private static object BuildUserResponse(
        IdentityUser user,
        IEnumerable<string> roles,
        Hydra.Domain.Entities.User? bankUser)
    {
        return new
        {
            identityUserId = user.Id,
            userId = bankUser?.Id.ToString() ?? user.Id,
            tenantId = bankUser?.TenantId,
            email = user.Email,
            roles = roles.ToArray(),
            tenantRole = bankUser?.Role.ToString()
        };
    }

    private async Task<BankUser?> FindBankUserAsync(IdentityUser user)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return null;
        }

        var email = user.Email.Trim().ToLower();

        return await _dbContext.BankUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(bankUser => bankUser.Email.ToLower() == email);
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
