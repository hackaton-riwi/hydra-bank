using Hydra.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Hydra.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private const string DefaultRole = "Customer";

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
        var user = new IdentityUser
        {
            UserName = request.Email,
            Email = request.Email
        };

        var result = await _userManager.CreateAsync(
            user,
            request.Password);

        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        await EnsureRoleExists(DefaultRole);
        await _userManager.AddToRoleAsync(user, DefaultRole);

        return Ok(new
        {
            message = "Usuario creado correctamente",
            role = DefaultRole
        });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginDto request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

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

        return Ok(new
        {
            token,
            expiresAt,
            user = new
            {
                id = user.Id,
                email = user.Email,
                roles
            }
        });
    }

    [HttpPost("roles")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateRole(CreateRoleDto request)
    {
        var roleName = request.Name.Trim();

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

    [HttpPost("users/{userId}/roles")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AssignRoleToUser(string userId, AssignRoleDto request)
    {
        var user = await _userManager.FindByIdAsync(userId);

        if (user is null)
        {
            return NotFound(new
            {
                message = "Usuario no encontrado"
            });
        }

        var roleName = request.Role.Trim();

        if (!await _roleManager.RoleExistsAsync(roleName))
        {
            return NotFound(new
            {
                message = "Rol no encontrado"
            });
        }

        if (await _userManager.IsInRoleAsync(user, roleName))
        {
            return Conflict(new
            {
                message = "El usuario ya tiene ese rol"
            });
        }

        var result = await _userManager.AddToRoleAsync(user, roleName);

        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        return Ok(new
        {
            message = "Rol asignado correctamente",
            userId,
            role = roleName
        });
    }

    private async Task EnsureRoleExists(string roleName)
    {
        if (!await _roleManager.RoleExistsAsync(roleName))
        {
            await _roleManager.CreateAsync(new IdentityRole(roleName));
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
}
