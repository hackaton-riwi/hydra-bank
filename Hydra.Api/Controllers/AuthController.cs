using Hydra.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private const string AdminRole = "ADMIN";
    private const string ClientRole = "CLIENT";

    private readonly UserManager<IdentityUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IConfiguration _configuration;

    public AuthController(
        UserManager<IdentityUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _configuration = configuration;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(RegisterDto request)
    {
        var email = request.Email.Trim();
        var role = await _userManager.Users.AnyAsync() ? ClientRole : AdminRole;
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
        var token = GenerateJwtToken(user, roles, expiresAt);

        return Ok(BuildAuthResponse(token, expiresAt, user, roles));
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
        var token = GenerateJwtToken(user, roles, expiresAt);

        return Ok(BuildAuthResponse(token, expiresAt, user, roles));
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

        return Ok(new
        {
            user = BuildUserResponse(user, roles)
        });
    }

    [HttpPost("roles")] 
    [Authorize(Roles = AdminRole)]
    public async Task<IActionResult> CreateRole(CreateRoleDto request)
    {
        var roleName = NormalizeRole(request.Name);

        if (string.IsNullOrWhiteSpace(roleName))
        {
            return BadRequest(new
            {
                message = "El nombre del rol es obligatorio"
            });
        }

        if (await _roleManager.RoleExistsAsync(roleName))
        {
            return Conflict(new
            {
                message = "El rol ya existe"
            });
        }

        var result = await _roleManager.CreateAsync(new IdentityRole(roleName));

        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        return Ok(new
        {
            message = "Rol creado correctamente",
            role = roleName
        });
    }
    
    private async Task EnsureDefaultRolesExist()
    {
        foreach (var roleName in new[] { AdminRole, ClientRole })
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                await _roleManager.CreateAsync(new IdentityRole(roleName));
            }
        }
    }

    private string GenerateJwtToken(IdentityUser user, IEnumerable<string> roles, DateTime expiresAt)
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
            new(ClaimTypes.Email, user.Email ?? string.Empty)
        };

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

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

    private static string NormalizeRole(string role)
    {
        return role.Trim().ToUpperInvariant();
    }

    private static object BuildAuthResponse(
        string token,
        DateTime expiresAt,
        IdentityUser user,
        IEnumerable<string> roles)
    {
        return new
        {
            token,
            expiresAt,
            user = BuildUserResponse(user, roles)
        };
    }

    private static object BuildUserResponse(IdentityUser user, IEnumerable<string> roles)
    {
        return new
        {
            id = user.Id,
            email = user.Email,
            roles = roles.ToArray()
        };
    }
}
