using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
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
    private readonly ITransactionService _transactionService;
    private string _lastCorrelationId = string.Empty;

    public AccountService(
        BankOsDbContext dbContext,
        IHttpContextAccessor httpContextAccessor,
        ITransactionService transactionService)
    {
        _dbContext = dbContext;
        _httpContextAccessor = httpContextAccessor;
        _transactionService = transactionService;
    }

    public string GetLastCorrelationId() => _lastCorrelationId;

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
        _dbContext.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Action = "ACCOUNT_CREATED",
            OldValue = null,
            NewValue = JsonSerializer.Serialize(new
            {
                AccountId = account.Id,
                AccountShortId = BuildShortId("ACC", account.Id),
                account.AccountNumber,
                account.OwnerId,
                OwnerShortId = BuildShortId("USR", account.OwnerId),
                account.Balance,
                account.Currency
            }),
            CreatedAt = now
        });
        await _dbContext.SaveChangesAsync();

        return Success("ACCOUNT_CREATED", "Cuenta creada correctamente", new
        {
            account.Id,
            account.AccountNumber,
            account.OwnerId,
            owner.FullName,
            owner.DocumentNumber,
            account.Balance,
            Status = account.Status.ToString(),
            account.CreatedAt
        });
    }

    // ─── MÉTODO MODIFICADO: BORRADO FÍSICO REAL EN POSTGRESQL ───
    public async Task<object> DeactivateAsync(string accountKey)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var accountId = await ResolveAccountIdAsync(tenantId, accountKey);
        
        // Buscamos la cuenta (Método administrativo seguro para ADMIN/SUPERADMIN)
        var account = await GetAccountForAdminAsync(tenantId, accountId);

        // Guardamos una copia exacta de los datos en memoria para el Log de Auditoría antes de eliminarla
        var accountDataLog = BuildAccount(account);

        // Indicamos a Entity Framework que remueva la entidad por completo
        _dbContext.Accounts.Remove(account);

        // Dejamos registro de auditoría de quién destruyó el registro
        _dbContext.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId, // ID del Administrador que gatilló el borrado
            Action = "ACCOUNT_DELETED_PERMANENTLY",
            OldValue = JsonSerializer.Serialize(accountDataLog),
            NewValue = null,
            CreatedAt = DateTime.UtcNow
        });

        // Guardamos los cambios. EF Core traducirá esto a un comando: DELETE FROM "Accounts" WHERE "Id" = ...
        await _dbContext.SaveChangesAsync();

        // Respondemos con éxito retornando la estructura de lo que fue borrado
        return Success("ACCOUNT_DELETED", "Cuenta eliminada permanentemente del sistema de base de datos", accountDataLog);
    }

    public async Task<object> RechargeAsync(RechargeAccountDto request)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        ValidateAmount(request.Amount);

        var account = await GetOwnAccountAsync(tenantId, userId);

        if (account.Status != AccountStatus.ACTIVE)
            throw new InvalidOperationException("Solo se puede recargar una cuenta activa");

        var previousBalance = account.Balance;
        account.Balance = RoundMoney(account.Balance + request.Amount);
        account.UpdatedAt = DateTime.UtcNow;
        _dbContext.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Action = "ACCOUNT_RECHARGED",
            OldValue = JsonSerializer.Serialize(new
            {
                AccountId = account.Id,
                PreviousBalance = previousBalance
            }),
            NewValue = JsonSerializer.Serialize(new
            {
                AccountId = account.Id,
                Amount = request.Amount,
                Balance = account.Balance
            }),
            CreatedAt = account.UpdatedAt
        });

        await _dbContext.SaveChangesAsync();

        return Success("ACCOUNT_RECHARGED", "Cuenta recargada correctamente", BuildAccount(account));
    }

    public async Task<object> GetMyAccountAsync()
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var account = await GetOwnAccountAsync(tenantId, userId);
        return Success("ACCOUNT_QUERIED", "Estado de la cuenta consultado correctamente", BuildAccount(account));
    }

    public async Task<object> GetTransactionsAsync()
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        const int limit = 100;

        var transactionsQuery = _dbContext.Transactions
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.UserId == userId);

        var total = await transactionsQuery.CountAsync();
        var items = await transactionsQuery
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .Select(x => new
            {
                id = BuildShortId("TRX", x.Id),
                Type = x.Type.ToString(),
                x.OriginalAmount,
                x.FeeAmount,
                sourceAccountId = x.SourceAccountId == null ? null : BuildShortId("ACC", x.SourceAccountId.Value),
                destinationAccountId = x.DestinationAccountId == null ? null : BuildShortId("ACC", x.DestinationAccountId.Value),
                Status = x.Status.ToString(),
                x.CreatedAt
            })
            .ToListAsync();

        return Success("TRANSACTION_HISTORY", "Historial consultado correctamente", new
        {
            limit,
            total,
            items
        });
    }

    public async Task<TransferResponseDto> TransferAsync(TransferRequestDto dto)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var headers = GetRequestHeaders();
        var result = await _transactionService.TransferAsync(tenantId, userId, dto, headers.idempotencyKey, headers.correlationId);
        _lastCorrelationId = headers.correlationId;
        return result;
    }

    public async Task<DepositResponseDto> DepositAsync(DepositRequestDto dto)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var headers = GetRequestHeaders();
        var result = await _transactionService.DepositAsync(tenantId, userId, dto, headers.idempotencyKey, headers.correlationId);
        _lastCorrelationId = headers.correlationId;
        return result;
    }

    public async Task<WithdrawResponseDto> WithdrawAsync(WithdrawRequestDto dto)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var headers = GetRequestHeaders();
        var result = await _transactionService.WithdrawAsync(tenantId, userId, dto, headers.idempotencyKey, headers.correlationId);
        _lastCorrelationId = headers.correlationId;
        return result;
    }

    private (string idempotencyKey, string correlationId) GetRequestHeaders()
    {
        var context = _httpContextAccessor.HttpContext;
        var idempotencyKey = context?.Request.Headers["Idempotency-Key"].FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        var correlationId = context?.Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        if (!Guid.TryParse(idempotencyKey, out _))
            throw new InvalidOperationException("Header Idempotency-Key debe ser un UUID");

        if (!Guid.TryParse(correlationId, out _))
            throw new InvalidOperationException("Header X-Correlation-ID debe ser un UUID");

        return (idempotencyKey, correlationId);
    }

    private async Task<Guid> ResolveAccountIdAsync(Guid tenantId, string accountKey)
    {
        var normalized = accountKey.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("La cuenta es obligatoria");

        if (Guid.TryParse(normalized, out var accountId))
            return accountId;

        var accountByNumber = await _dbContext.Accounts
            .AsNoTracking()
            .Where(account =>
                account.TenantId == tenantId &&
                account.AccountNumber == normalized)
            .Select(account => (Guid?)account.Id)
            .SingleOrDefaultAsync();

        if (accountByNumber.HasValue)
            return accountByNumber.Value;

        var shortCode = normalized.ToUpperInvariant();
        const string prefix = "ACC-";
        if (shortCode.StartsWith(prefix, StringComparison.Ordinal))
            shortCode = shortCode[prefix.Length..];

        if (shortCode.Length < 8)
            throw new InvalidOperationException("La cuenta debe ser un número de cuenta, GUID o código corto tipo ACC-1234ABCD");

        // Solución Postgres segura trayendo la lista reducida indexada por Tenant a memoria local
        var tenantAccountIds = await _dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.TenantId == tenantId)
            .Select(account => account.Id)
            .ToListAsync();

        var matches = tenantAccountIds
            .Where(id => id.ToString("N").StartsWith(shortCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Cuenta no encontrada con la clave: {accountKey}"),
            _ => throw new InvalidOperationException("El código corto de cuenta es ambiguo; usa más caracteres del GUID")
        };
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

    private async Task<Account> GetAccountForAdminAsync(Guid tenantId, Guid accountId)
    {
        return await _dbContext.Accounts
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == accountId)
            ?? throw new InvalidOperationException("La cuenta solicitada no existe en este tenant.");
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
            FullName = account.User?.FullName,
            DocumentNumber = account.User?.DocumentNumber,
            account.Balance,
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

    private static string BuildShortId(string prefix, Guid id)
    {
        return $"{prefix}-{id.ToString("N")[..8].ToUpperInvariant()}";
    }
}