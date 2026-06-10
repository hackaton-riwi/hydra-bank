using System.Security.Claims;
using Hydra.Application.DTOs;
using Hydra.Application.Interfaces;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Infrastructure.DATA;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Hydra.Application.Services;

public class AccountService : IAccountService
{
    public const string DefaultCurrency = "COP";

    private readonly BankOsDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AccountService(
        BankOsDbContext dbContext,
        IHttpContextAccessor httpContextAccessor)
    {
        _dbContext = dbContext;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<object> CreateAsync(CreateAccountDto request)
    {
        var (tenantId, userId) = GetCurrentTenantUser();

        var owner = await _dbContext.BankUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.Id == userId &&
                x.TenantId == tenantId &&
                x.Role == UserRole.CLIENT);

        if (owner is null)
            throw new UnauthorizedAccessException("Solo un cliente vinculado al tenant puede crear su propia cuenta");

        var existingAccount = await _dbContext.Accounts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OwnerId == owner.Id);

        if (existingAccount is not null)
            throw new InvalidOperationException("Este documento ya tiene una cuenta creada");

        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OwnerId = userId,
            AccountNumber = GenerateAccountNumber(),
            Balance = 0,
            Currency = DefaultCurrency,
            Status = AccountStatus.ACTIVE,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Accounts.Add(account);
        await _dbContext.SaveChangesAsync();

        return Success("ACCOUNT_CREATED", "Cuenta creada correctamente", new
        {
            account.Id,
            account.AccountNumber,
            account.OwnerId,
            owner.FullName,
            owner.DocumentNumber,
            account.Balance,
            account.Currency,
            Status = account.Status.ToString(),
            account.CreatedAt
        });
    }

    public async Task<object> DeactivateAsync(Guid accountId)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var account = await GetOwnAccountAsync(tenantId, userId, accountId);

        if (account.Status != AccountStatus.ACTIVE)
            return Success("ACCOUNT_ALREADY_INACTIVE", "La cuenta ya no se encuentra activa", BuildAccount(account));

        account.Status = AccountStatus.INACTIVE;
        account.DeactivatedAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        return Success("ACCOUNT_DEACTIVATED", "Cuenta desactivada correctamente", BuildAccount(account));
    }

    public async Task<object> RechargeAsync(RechargeAccountDto request)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        ValidateAmount(request.Amount);

        var account = await GetOwnAccountAsync(tenantId, userId);

        if (account.Status != AccountStatus.ACTIVE)
            throw new InvalidOperationException("Solo se puede recargar una cuenta activa");

        account.Balance = RoundMoney(account.Balance + request.Amount);
        account.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        return Success("ACCOUNT_RECHARGED", "Cuenta recargada correctamente", BuildAccount(account));
    }

    public async Task<object> GetTransactionsAsync(TransactionHistoryQueryDto query)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var limit = Math.Clamp(query.Limit, 1, 100);
        var offset = Math.Max(query.Offset, 0);

        var transactionsQuery = _dbContext.Transactions
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.UserId == userId);

        if (query.From.HasValue)
            transactionsQuery = transactionsQuery.Where(x => x.CreatedAt >= query.From.Value);

        if (query.To.HasValue)
            transactionsQuery = transactionsQuery.Where(x => x.CreatedAt <= query.To.Value);

        if (query.Type.HasValue)
            transactionsQuery = transactionsQuery.Where(x => x.Type == query.Type.Value);

        var total = await transactionsQuery.CountAsync();
        var items = await transactionsQuery
            .OrderByDescending(x => x.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(x => new
            {
                x.Id,
                Type = x.Type.ToString(),
                x.OriginalAmount,
                x.FeeAmount,
                x.SourceAccountId,
                x.DestinationAccountId,
                Status = x.Status.ToString(),
                x.CreatedAt
            })
            .ToListAsync();

        return Success("TRANSACTION_HISTORY", "Historial consultado correctamente", new
        {
            limit,
            offset,
            total,
            items
        });
    }

    private async Task<Account> GetOwnAccountAsync(Guid tenantId, Guid userId, Guid accountId)
    {
        return await _dbContext.Accounts
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == accountId && x.OwnerId == userId)
            ?? throw new InvalidOperationException("Cuenta no encontrada para el cliente autenticado");
    }

    private async Task<Account> GetOwnAccountAsync(Guid tenantId, Guid userId)
    {
        return await _dbContext.Accounts
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OwnerId == userId)
            ?? throw new InvalidOperationException("El cliente autenticado todavía no tiene cuenta");
    }

    private (Guid TenantId, Guid UserId) GetCurrentTenantUser()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        var tenantIdClaim = user?.FindFirst("tenant_id")?.Value;
        var userIdClaim = user?.FindFirst("user_id")?.Value;

        if (!Guid.TryParse(tenantIdClaim, out var tenantId) ||
            !Guid.TryParse(userIdClaim, out var userId))
        {
            throw new UnauthorizedAccessException("El token no contiene tenant_id y user_id válidos");
        }

        return (tenantId, userId);
    }

    private static void ValidateAmount(decimal amount)
    {
        if (amount <= 0)
            throw new InvalidOperationException("El monto debe ser mayor que cero");
    }

    private static decimal RoundMoney(decimal amount)
    {
        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    private static string GenerateAccountNumber()
    {
        return DateTime.UtcNow.Ticks.ToString()[^10..];
    }

    private static object BuildAccount(Account account)
    {
        return new
        {
            account.Id,
            account.AccountNumber,
            account.OwnerId,
            account.User.FullName,
            account.User.DocumentNumber,
            account.Balance,
            account.Currency,
            Status = account.Status.ToString(),
            account.CreatedAt,
            account.UpdatedAt,
            account.DeactivatedAt
        };
    }

    private static object Success(string code, string description, object data)
    {
        return new
        {
            success = true,
            code,
            description,
            data
        };
    }

}
