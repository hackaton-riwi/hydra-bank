using System;
using System.Collections.Generic;
using System.Linq;
using Hydra.Application.Interfaces;
using Hydra.Application.Services;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Infrastructure;
using Hydra.Infrastructure.DATA;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using StackExchange.Redis;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BankUser = Hydra.Domain.Entities.User;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// 1. Nombre de política limpio y seguro
const string CorsPolicyName = "AllowAll";

builder.Services.AddControllers();

// 2. Un solo bloque de configuración de CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Name = "Authorization",
        Description = "Pega solo el token JWT, sin escribir Bearer"
    });

    options.AddSecurityRequirement(openApiDocument => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer", openApiDocument),
            new List<string>()
        }
    });
});
    
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key no está configurado");

if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException("Jwt:Key debe tener mínimo 32 caracteres para HmacSha256");
}
builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<IAccountService, AccountService>();

var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = "HydraBank:";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtKey!)),
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = ClaimTypes.Role
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var jti = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);

                if (string.IsNullOrWhiteSpace(jti))
                {
                    return;
                }

                var cache = context.HttpContext.RequestServices.GetRequiredService<IDistributedCache>();
                var revokedToken = await cache.GetStringAsync(BuildRevokedTokenCacheKey(jti));

                if (revokedToken is not null)
                {
                    context.Fail("Token revocado");
                }
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", httpContext =>
    {
        var partitionKey = GetRateLimitPartitionKey(httpContext);

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });

    options.AddPolicy("financial", httpContext =>
    {
        var partitionKey = GetRateLimitPartitionKey(httpContext);

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetSection("Redis")["Configuration"] ?? "localhost:6379"));

builder.Services.AddScoped<IIdempotencyService, IdempotencyService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddHttpClient<IWebhookNotifier, WebhookNotifier>();

var app = builder.Build();

await SeedIdentityRolesAsync(app);
await SeedSuperAdminAsync(app);

var swaggerEnabled = app.Environment.IsDevelopment()
    || app.Configuration.GetValue<bool>("Swagger:Enabled");

if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseRouting();

// 3. ORDEN DE MIDDLEWARES CORREGIDO:
// CORS va de primero para responder las peticiones OPTIONS previas del navegador.
app.UseCors(CorsPolicyName);

// El Rate Limiter va antes de la Auth para mitigar ataques de fuerza bruta en el login de forma eficiente.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Forzamos que los controladores requieran la política global de CORS
app.MapControllers().RequireCors(CorsPolicyName);

app.Run();

static async Task SeedIdentityRolesAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    foreach (var roleName in new[] { "SUPERADMIN", "ADMIN", "CLIENT" })
    {
        if (!await roleManager.RoleExistsAsync(roleName))
        {
            await roleManager.CreateAsync(new IdentityRole(roleName));
        }
    }
}

static async Task SeedSuperAdminAsync(WebApplication app)
{
    const string tenantName = "Hydra Bank";
    const string tenantSlug = "hydra-bank";
    const string email = "admin@hydra.test";
    const string password = "hydra123*";
    const string superAdminRole = "SUPERADMIN";

    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<BankOsDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var bankPasswordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<BankUser>>();

    if (!await roleManager.RoleExistsAsync(superAdminRole))
    {
        await roleManager.CreateAsync(new IdentityRole(superAdminRole));
    }

    var now = DateTime.UtcNow;
    var tenant = await dbContext.Tenants.SingleOrDefaultAsync(x => x.Slug == tenantSlug);

    if (tenant is null)
    {
        tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = tenantName,
            Slug = tenantSlug,
            MainCurrency = "COP",
            MaxTransactionAmount = 5_000_000m,
            FeeType = FeeTypeEnum.FIXED,
            FeeValue = 0m,
            WebhookUrl = null,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync();
    }

    var bankUser = await dbContext.BankUsers.SingleOrDefaultAsync(x =>
        x.TenantId == tenant.Id &&
        x.Email.ToLower() == email);

    if (bankUser is null)
    {
        bankUser = new BankUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            FullName = "Super Administrador Hydra",
            DocumentNumber = "SUPERADMIN-HYDRA-BANK",
            Email = email,
            Role = UserRole.ADMIN,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.BankUsers.Add(bankUser);
    }

    bankUser.PasswordHash = bankPasswordHasher.HashPassword(bankUser, password);
    bankUser.UpdatedAt = now;
    await dbContext.SaveChangesAsync();

    var identityUserName = $"{tenantSlug}:{email}";
    var identityUser = await userManager.FindByNameAsync(identityUserName);

    if (identityUser is null)
    {
        identityUser = new IdentityUser
        {
            UserName = identityUserName,
            Email = email,
            EmailConfirmed = true
        };

        var createResult = await userManager.CreateAsync(identityUser);

        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"No se pudo crear el superadmin: {string.Join(", ", createResult.Errors.Select(x => x.Description))}");
        }

        identityUser.PasswordHash = userManager.PasswordHasher.HashPassword(identityUser, password);
        await userManager.UpdateAsync(identityUser);
        await userManager.UpdateSecurityStampAsync(identityUser);
    }
    else
    {
        identityUser.Email = email;
        identityUser.EmailConfirmed = true;
        identityUser.PasswordHash = userManager.PasswordHasher.HashPassword(identityUser, password);
        await userManager.UpdateAsync(identityUser);
        await userManager.UpdateSecurityStampAsync(identityUser);
    }

    if (!await userManager.IsInRoleAsync(identityUser, superAdminRole))
    {
        var roleResult = await userManager.AddToRoleAsync(identityUser, superAdminRole);

        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"No se pudo asignar SUPERADMIN: {string.Join(", ", roleResult.Errors.Select(x => x.Description))}");
        }
    }
}

static string GetRateLimitPartitionKey(HttpContext httpContext)
{
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    if (!string.IsNullOrWhiteSpace(userId))
    {
        return $"user:{userId}";
    }

    return $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}

static string BuildRevokedTokenCacheKey(string jti)
{
    return $"revoked-token:{jti}";
}
