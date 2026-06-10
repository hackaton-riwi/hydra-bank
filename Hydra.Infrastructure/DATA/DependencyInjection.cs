using Hydra.Infrastructure.DATA;
using Hydra.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hydra.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<BankOsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsqlOptions => npgsqlOptions
                    .MapEnum<AccountStatus>("account_status")
                    .MapEnum<FeeTypeEnum>("fee_type_enum")
                    .MapEnum<IdempotencyState>("idempotency_state")
                    .MapEnum<TransactionStatus>("transaction_status")
                    .MapEnum<TransactionType>("transaction_type")
                    .MapEnum<UserRole>("user_role")));

        services.AddIdentityCore<IdentityUser>(options =>
            {
                options.Password.RequiredLength = 6;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<BankOsDbContext>()
            .AddDefaultTokenProviders();

        return services;
    }
}
