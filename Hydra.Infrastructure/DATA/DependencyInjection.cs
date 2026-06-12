using Hydra.Infrastructure.DATA;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.NameTranslation;

namespace Hydra.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var enumNameTranslator = new NpgsqlNullNameTranslator();

        services.AddDbContext<BankOsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsqlOptions => npgsqlOptions
                    .MapEnum<AccountStatus>("account_status", nameTranslator: enumNameTranslator)
                    .MapEnum<FeeTypeEnum>("fee_type_enum", nameTranslator: enumNameTranslator)
                    .MapEnum<IdempotencyState>("idempotency_state", nameTranslator: enumNameTranslator)
                    .MapEnum<TransactionStatus>("transaction_status", nameTranslator: enumNameTranslator)
                    .MapEnum<TransactionType>("transaction_type", nameTranslator: enumNameTranslator)
                    .MapEnum<UserRole>("user_role", nameTranslator: enumNameTranslator)));

        services.AddIdentityCore<IdentityUser>(options =>
            {
                options.User.AllowedUserNameCharacters =
                    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+:";
                options.Password.RequiredLength = 6;
                options.Password.RequireDigit = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireNonAlphanumeric = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<BankOsDbContext>()
            .AddDefaultTokenProviders();

        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

        return services;
    }
}
