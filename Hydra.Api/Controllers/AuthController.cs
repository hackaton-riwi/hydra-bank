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
    private const string DefaultCurrency = "COP";

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
        var documentNumber = NormalizeDocumentNumber(request.DocumentNumber);

        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            return BadRequest(Error("DOCUMENT_REQUIRED", "El número de documento es obligatorio"));
        }

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

        if (await _dbContext.BankUsers.AnyAsync(x => x.TenantId == tenant.Id && x.DocumentNumber == documentNumber))
        {
            return Conflict(Error("DOCUMENT_ALREADY_EXISTS", "Ya existe un usuario con ese número de documento"));
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
            DocumentNumber = documentNumber,
            Email = email,
            Role = UserRole.CLIENT,
            CreatedAt = now,
            UpdatedAt = now
        };
        bankUser.PasswordHash = _passwordHasher.HashPassword(bankUser, request.Password);

        var account = new Hydra.Domain.Entities.Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            OwnerId = bankUser.Id,
            AccountNumber = GenerateAccountNumber(),
            Balance = 0,
            Currency = DefaultCurrency,
            Status = AccountStatus.ACTIVE,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        _dbContext.BankUsers.Add(bankUser);
        _dbContext.Accounts.Add(account);
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

        return Created("/api/v1/auth/register", BuildRegistrationResponse(identityUser, bankUser, account));
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
        var account = await _dbContext.Accounts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == bankUser.TenantId && x.OwnerId == bankUser.Id);

        return Ok(BuildAuthResponse(token, expiresAt, user, roles, bankUser, account));
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
        var account = bankUser is null
            ? null
            : await _dbContext.Accounts
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == bankUser.TenantId && x.OwnerId == bankUser.Id);

        return Ok(new
        {
            user = BuildUserResponse(user, roles, bankUser),
            account = account is null ? null : BuildAccountResponse(account)
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
        Hydra.Domain.Entities.User? bankUser,
        Hydra.Domain.Entities.Account? account = null)
    {
        return new
        {
            token,
            expiresAt,
            user = BuildUserResponse(user, roles, bankUser),
            account = account is null ? null : BuildAccountResponse(account)
        };
    }

    private static object BuildRegistrationResponse(
        IdentityUser user,
        Hydra.Domain.Entities.User bankUser,
        Hydra.Domain.Entities.Account account)
    {
        return new
        {
            success = true,
            code = "CLIENT_REGISTERED",
            description = "Cliente registrado correctamente. Debe iniciar sesión para obtener token.",
            user = new
            {
                identityUserId = user.Id,
                userId = bankUser.Id,
                bankUser.TenantId,
                bankUser.FullName,
                bankUser.DocumentNumber,
                bankUser.Email,
                tenantRole = bankUser.Role.ToString()
            },
            account = BuildAccountResponse(account)
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
            fullName = bankUser?.FullName,
            documentNumber = bankUser?.DocumentNumber,
            email = user.Email,
            roles = roles.ToArray(),
            tenantRole = bankUser?.Role.ToString()
        };
    }

    private static object BuildAccountResponse(Hydra.Domain.Entities.Account account)
    {
        return new
        {
            account.Id,
            account.AccountNumber,
            account.OwnerId,
            account.Balance,
            account.Currency,
            Status = account.Status.ToString(),
            account.CreatedAt
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

    private static string NormalizeDocumentNumber(string documentNumber)
    {
        var normalized = documentNumber.Trim().ToUpperInvariant();

        return normalized;
    }

    private static string GenerateAccountNumber()
    {
        return DateTime.UtcNow.Ticks.ToString()[^10..];
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
