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

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
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

    public AuthController(
        UserManager<IdentityUser> userManager,
        RoleManager<IdentityRole> roleManager,
        BankOsDbContext dbContext,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _dbContext = dbContext;
        _configuration = configuration;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(RegisterDto request)
    {
        var email = request.Email.Trim();

        if (await _userManager.Users.AnyAsync())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "El registro público solo está habilitado para crear el primer SUPERADMIN. Los usuarios de tenant deben crearse dentro de su institución."
            });
        }

        var role = SuperAdminRole;
        var user = new IdentityUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(
            user,
            request.Password);

        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        await EnsureDefaultRolesExist();
        await _userManager.AddToRoleAsync(user, role);

        var roles = await _userManager.GetRolesAsync(user);
        var expiresAt = DateTime.UtcNow.AddMinutes(GetTokenExpirationMinutes());
        var bankUser = await FindBankUserAsync(user);
        var token = GenerateJwtToken(user, roles, bankUser, expiresAt);

        return Ok(BuildAuthResponse(token, expiresAt, user, roles, bankUser));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginDto request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email.Trim());

        if (user is null || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            return Unauthorized(new
            {
                message = "Credenciales inválidas"
            });
        }

        var roles = await _userManager.GetRolesAsync(user);
        var expiresAt = DateTime.UtcNow.AddMinutes(GetTokenExpirationMinutes());
        var bankUser = await FindBankUserAsync(user);
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

    private async Task<Hydra.Domain.Entities.User?> FindBankUserAsync(IdentityUser user)
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
}
