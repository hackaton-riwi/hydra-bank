using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydra.Application.Caching;
using Hydra.Application.DTOs;
using Hydra.Application.Interfaces;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Infrastructure.DATA;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.EntityFrameworkCore;

namespace Hydra.Application.Services;

public class AccountService : IAccountService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BankOsDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDistributedCache _cache;

    public AccountService(
        BankOsDbContext dbContext,
        IHttpContextAccessor httpContextAccessor,
        IDistributedCache cache)
    {
        _dbContext = dbContext;
        _httpContextAccessor = httpContextAccessor;
        _cache = cache;
    }

    public async Task<object> CreateAsync(CreateAccountDto request)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var currency = NormalizeCurrency(request.Currency);

        var owner = await _dbContext.BankUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.Id == userId &&
                x.TenantId == tenantId &&
                x.Role == UserRole.CLIENT);

        if (owner is null)
            throw new UnauthorizedAccessException("Solo un cliente vinculado al tenant puede crear su propia cuenta");

        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OwnerId = userId,
            AccountNumber = GenerateAccountNumber(),
            Balance = 0,
            Currency = currency,
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

    public Task<ServiceResponse> DepositAsync(Guid accountId, MoneyOperationDto request, Guid idempotencyKey)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var requestHash = ComputeHash($"deposit:{tenantId}:{userId}:{accountId}:{request.Amount}");

        return ExecuteIdempotentAsync(tenantId, userId, idempotencyKey, requestHash, async () =>
        {
            ValidateAmount(request.Amount);
            await ValidateTenantLimitAsync(tenantId, request.Amount);

            await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var account = await GetOwnAccountAsync(tenantId, userId, accountId);
            if (account.Status != AccountStatus.ACTIVE)
            {
                var failed = NewTransaction(tenantId, userId, TransactionType.DEPOSIT, request.Amount, null, account.Id, null, null, 0, TransactionStatus.FAILED, idempotencyKey);
                _dbContext.Transactions.Add(failed);
                await _dbContext.SaveChangesAsync();
                await dbTransaction.CommitAsync();
                return new ServiceResponse(StatusCodes.Status400BadRequest, Error("ACCOUNT_NOT_ACTIVE", "No se permiten operaciones financieras sobre cuentas inactivas o bloqueadas"));
            }

            account.Balance = RoundMoney(account.Balance + request.Amount);
            account.UpdatedAt = DateTime.UtcNow;

            var transaction = NewTransaction(
                tenantId,
                userId,
                TransactionType.DEPOSIT,
                request.Amount,
                null,
                account.Id,
                null,
                null,
                0,
                TransactionStatus.SUCCESS,
                idempotencyKey);

            _dbContext.Transactions.Add(transaction);
            await _dbContext.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return new ServiceResponse(StatusCodes.Status200OK, Success("DEPOSIT_SUCCESS", "Depósito realizado correctamente", new
            {
                transaction.Id,
                accountId = account.Id,
                account.Balance,
                transaction.OriginalAmount,
                transaction.FeeAmount,
                transaction.CreatedAt
            }));
        });
    }

    public Task<ServiceResponse> WithdrawAsync(Guid accountId, MoneyOperationDto request, Guid idempotencyKey)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var requestHash = ComputeHash($"withdraw:{tenantId}:{userId}:{accountId}:{request.Amount}");

        return ExecuteIdempotentAsync(tenantId, userId, idempotencyKey, requestHash, async () =>
        {
            ValidateAmount(request.Amount);
            await ValidateTenantLimitAsync(tenantId, request.Amount);

            await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var account = await GetOwnAccountAsync(tenantId, userId, accountId);
            if (account.Status != AccountStatus.ACTIVE)
            {
                var failed = NewTransaction(tenantId, userId, TransactionType.WITHDRAW, request.Amount, account.Id, null, null, null, 0, TransactionStatus.FAILED, idempotencyKey);
                _dbContext.Transactions.Add(failed);
                await _dbContext.SaveChangesAsync();
                await dbTransaction.CommitAsync();
                return new ServiceResponse(StatusCodes.Status400BadRequest, Error("ACCOUNT_NOT_ACTIVE", "No se permiten operaciones financieras sobre cuentas inactivas o bloqueadas"));
            }

            if (account.Balance < request.Amount)
            {
                var failed = NewTransaction(tenantId, userId, TransactionType.WITHDRAW, request.Amount, account.Id, null, null, null, 0, TransactionStatus.FAILED, idempotencyKey);
                _dbContext.Transactions.Add(failed);
                await _dbContext.SaveChangesAsync();
                await dbTransaction.CommitAsync();
                return new ServiceResponse(StatusCodes.Status400BadRequest, Error("INSUFFICIENT_FUNDS", "Saldo insuficiente"));
            }

            account.Balance = RoundMoney(account.Balance - request.Amount);
            account.UpdatedAt = DateTime.UtcNow;

            var transaction = NewTransaction(
                tenantId,
                userId,
                TransactionType.WITHDRAW,
                request.Amount,
                account.Id,
                null,
                null,
                null,
                0,
                TransactionStatus.SUCCESS,
                idempotencyKey);

            _dbContext.Transactions.Add(transaction);
            await _dbContext.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return new ServiceResponse(StatusCodes.Status200OK, Success("WITHDRAW_SUCCESS", "Retiro realizado correctamente", new
            {
                transaction.Id,
                accountId = account.Id,
                account.Balance,
                transaction.OriginalAmount,
                transaction.FeeAmount,
                transaction.CreatedAt
            }));
        });
    }

    public Task<ServiceResponse> TransferAsync(TransferDto request, Guid idempotencyKey)
    {
        var (tenantId, userId) = GetCurrentTenantUser();
        var requestHash = ComputeHash($"transfer:{tenantId}:{userId}:{request.SourceAccountId}:{request.DestinationAccountId}:{request.Amount}");

        return ExecuteIdempotentAsync(tenantId, userId, idempotencyKey, requestHash, async () =>
        {
            ValidateAmount(request.Amount);
            await ValidateTenantLimitAsync(tenantId, request.Amount);

            if (request.SourceAccountId == request.DestinationAccountId)
                throw new InvalidOperationException("La cuenta origen y destino deben ser diferentes");

            await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            var tenant = await GetTenantConfigAsync(tenantId);

            var source = await GetOwnAccountAsync(tenantId, userId, request.SourceAccountId);
            var destination = await _dbContext.Accounts
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.DestinationAccountId)
                ?? throw new InvalidOperationException("Cuenta destino no encontrada");

            var fee = CalculateFee(tenant, request.Amount);
            var totalDebit = RoundMoney(request.Amount + fee);

            if (source.Status != AccountStatus.ACTIVE || destination.Status != AccountStatus.ACTIVE)
            {
                var failed = NewTransaction(tenantId, userId, TransactionType.TRANSFER, request.Amount, source.Id, destination.Id, null, null, fee, TransactionStatus.FAILED, idempotencyKey);
                _dbContext.Transactions.Add(failed);
                await _dbContext.SaveChangesAsync();
                await dbTransaction.CommitAsync();
                return new ServiceResponse(StatusCodes.Status400BadRequest, Error("ACCOUNT_NOT_ACTIVE", "Ambas cuentas deben estar activas para transferir"));
            }

            if (source.Balance < totalDebit)
            {
                var failed = NewTransaction(tenantId, userId, TransactionType.TRANSFER, request.Amount, source.Id, destination.Id, null, null, fee, TransactionStatus.FAILED, idempotencyKey);
                _dbContext.Transactions.Add(failed);
                await _dbContext.SaveChangesAsync();
                await dbTransaction.CommitAsync();
                return new ServiceResponse(StatusCodes.Status400BadRequest, Error("INSUFFICIENT_FUNDS", "Saldo insuficiente para cubrir el monto y la comisión"));
            }

            var exchangeRate = await GetExchangeRateOrNullAsync(tenantId, source.Currency, destination.Currency);
            if (exchangeRate is null)
            {
                var failed = NewTransaction(tenantId, userId, TransactionType.TRANSFER, request.Amount, source.Id, destination.Id, null, null, fee, TransactionStatus.FAILED, idempotencyKey);
                _dbContext.Transactions.Add(failed);
                await _dbContext.SaveChangesAsync();
                await dbTransaction.CommitAsync();
                return new ServiceResponse(StatusCodes.Status400BadRequest, Error("EXCHANGE_RATE_NOT_FOUND", $"No existe tasa de cambio configurada para {source.Currency}->{destination.Currency}"));
            }

            var appliedExchangeRate = exchangeRate.Value;
            var convertedAmount = RoundMoney(request.Amount * appliedExchangeRate);

            source.Balance = RoundMoney(source.Balance - totalDebit);
            source.UpdatedAt = DateTime.UtcNow;
            destination.Balance = RoundMoney(destination.Balance + convertedAmount);
            destination.UpdatedAt = DateTime.UtcNow;

            var transaction = NewTransaction(
                tenantId,
                userId,
                TransactionType.TRANSFER,
                request.Amount,
                source.Id,
                destination.Id,
                convertedAmount,
                appliedExchangeRate == 1m ? null : appliedExchangeRate,
                fee,
                TransactionStatus.SUCCESS,
                idempotencyKey);

            _dbContext.Transactions.Add(transaction);
            await _dbContext.SaveChangesAsync();
            await dbTransaction.CommitAsync();

            return new ServiceResponse(StatusCodes.Status200OK, Success("TRANSFER_SUCCESS", "Transferencia realizada correctamente", new
            {
                transaction.Id,
                sourceAccountId = source.Id,
                destinationAccountId = destination.Id,
                sourceBalance = source.Balance,
                destinationBalance = destination.Balance,
                originalAmount = request.Amount,
                convertedAmount,
                exchangeRate = appliedExchangeRate == 1m ? null : (decimal?)appliedExchangeRate,
                feeAmount = fee,
                transaction.CreatedAt
            }));
        });
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
                x.ConvertedAmount,
                x.ExchangeRate,
                x.FeeAmount,
                x.SourceAccountId,
                x.DestinationAccountId,
                Status = x.Status.ToString(),
                x.IdempotencyKey,
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

    private async Task<ServiceResponse> ExecuteIdempotentAsync(
        Guid tenantId,
        Guid userId,
        Guid idempotencyKey,
        string requestHash,
        Func<Task<ServiceResponse>> operation)
    {
        var now = DateTime.UtcNow;
        var record = new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            State = IdempotencyState.PROCESSING,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        };

        _dbContext.IdempotencyRecords.Add(record);

        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(record).State = EntityState.Detached;

            var existing = await _dbContext.IdempotencyRecords
                .AsNoTracking()
                .SingleAsync(x =>
                    x.TenantId == tenantId &&
                    x.UserId == userId &&
                    x.IdempotencyKey == idempotencyKey);

            if (existing.ExpiresAt <= now)
                return new ServiceResponse(StatusCodes.Status409Conflict, Error("IDEMPOTENCY_KEY_EXPIRED", "La Idempotency-Key ya expiró"));

            if (existing.RequestHash != requestHash)
                return new ServiceResponse(StatusCodes.Status409Conflict, Error("IDEMPOTENCY_KEY_REUSED", "La Idempotency-Key fue usada con una solicitud diferente"));

            if (existing.State == IdempotencyState.PROCESSING)
                return new ServiceResponse(StatusCodes.Status409Conflict, Error("TRANSACTION_IN_PROGRESS", "La transacción se encuentra en curso"));

            var body = string.IsNullOrWhiteSpace(existing.ResponseBody)
                ? Error("IDEMPOTENCY_RESPONSE_NOT_FOUND", "No se encontró la respuesta original")
                : JsonSerializer.Deserialize<JsonElement>(existing.ResponseBody, JsonOptions);

            return new ServiceResponse(existing.StatusCode ?? StatusCodes.Status200OK, body);
        }

        try
        {
            var response = await operation();
            record.State = IdempotencyState.COMPLETED;
            record.StatusCode = response.StatusCode;
            record.ResponseBody = JsonSerializer.Serialize(response.Body, JsonOptions);
            await _dbContext.SaveChangesAsync();

            return response;
        }
        catch (Exception exception)
        {
            _dbContext.ChangeTracker.Clear();

            var storedRecord = await _dbContext.IdempotencyRecords
                .SingleAsync(x =>
                    x.TenantId == tenantId &&
                    x.UserId == userId &&
                    x.IdempotencyKey == idempotencyKey);

            var failedBody = Error("FINANCIAL_OPERATION_FAILED", exception.Message);
            storedRecord.State = IdempotencyState.COMPLETED;
            storedRecord.StatusCode = StatusCodes.Status400BadRequest;
            storedRecord.ResponseBody = JsonSerializer.Serialize(failedBody, JsonOptions);
            await _dbContext.SaveChangesAsync();

            return new ServiceResponse(StatusCodes.Status400BadRequest, failedBody);
        }
    }

    private async Task<Account> GetOwnAccountAsync(Guid tenantId, Guid userId, Guid accountId)
    {
        return await _dbContext.Accounts
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == accountId && x.OwnerId == userId)
            ?? throw new InvalidOperationException("Cuenta no encontrada para el cliente autenticado");
    }

    private async Task ValidateTenantLimitAsync(Guid tenantId, decimal amount)
    {
        var tenant = await GetTenantConfigAsync(tenantId);

        if (amount > tenant.MaxTransactionAmount)
            throw new InvalidOperationException("El monto supera el límite máximo permitido por el tenant");
    }

    private async Task<decimal?> GetExchangeRateOrNullAsync(Guid tenantId, string fromCurrency, string toCurrency)
    {
        if (fromCurrency == toCurrency)
            return 1m;

        var cacheKey = BankCacheKeys.ExchangeRate(tenantId, fromCurrency, toCurrency);
        var cachedRate = await _cache.GetStringAsync(cacheKey);

        if (decimal.TryParse(cachedRate, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedRate))
            return parsedRate;

        var rate = await _dbContext.ExchangeRates
            .AsNoTracking()
            .Where(x =>
                x.TenantId == tenantId &&
                x.FromCurrency == fromCurrency &&
                x.ToCurrency == toCurrency)
            .Select(x => (decimal?)x.Rate)
            .SingleOrDefaultAsync();

        if (rate.HasValue)
        {
            await _cache.SetStringAsync(
                cacheKey,
                rate.Value.ToString(CultureInfo.InvariantCulture),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
                });
        }

        return rate;
    }

    private async Task<TenantConfigCacheDto> GetTenantConfigAsync(Guid tenantId)
    {
        var cacheKey = BankCacheKeys.TenantConfig(tenantId);
        var cachedTenant = await _cache.GetStringAsync(cacheKey);

        if (!string.IsNullOrWhiteSpace(cachedTenant))
        {
            var parsedTenant = JsonSerializer.Deserialize<TenantConfigCacheDto>(cachedTenant, JsonOptions);
            if (parsedTenant is not null)
                return parsedTenant;
        }

        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .Where(x => x.Id == tenantId)
            .Select(x => new TenantConfigCacheDto(x.Id, x.MaxTransactionAmount, x.FeeType, x.FeeValue))
            .SingleAsync();

        await _cache.SetStringAsync(
            cacheKey,
            JsonSerializer.Serialize(tenant, JsonOptions),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
            });

        return tenant;
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
            throw new InvalidOperationException("El monto debe ser estrictamente positivo");
    }

    private static void EnsureActive(Account account)
    {
        if (account.Status != AccountStatus.ACTIVE)
            throw new InvalidOperationException("No se permiten operaciones financieras sobre cuentas inactivas o bloqueadas");
    }

    private static decimal CalculateFee(TenantConfigCacheDto tenant, decimal amount)
    {
        var fee = tenant.FeeType == FeeTypeEnum.PERCENTAGE
            ? amount * tenant.FeeValue / 100m
            : tenant.FeeValue;

        return RoundMoney(fee);
    }

    private static Transaction NewTransaction(
        Guid tenantId,
        Guid userId,
        TransactionType type,
        decimal originalAmount,
        Guid? sourceAccountId,
        Guid? destinationAccountId,
        decimal? convertedAmount,
        decimal? exchangeRate,
        decimal feeAmount,
        TransactionStatus status,
        Guid idempotencyKey)
    {
        return new Transaction
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Type = type,
            SourceAccountId = sourceAccountId,
            DestinationAccountId = destinationAccountId,
            OriginalAmount = RoundMoney(originalAmount),
            ConvertedAmount = convertedAmount,
            ExchangeRate = exchangeRate,
            FeeAmount = feeAmount,
            Status = status,
            IdempotencyKey = idempotencyKey,
            CorrelationId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string NormalizeCurrency(string currency)
    {
        var normalized = currency.Trim().ToUpperInvariant();

        if (normalized.Length != 3 || normalized.Any(x => x is < 'A' or > 'Z'))
            throw new InvalidOperationException("La moneda debe usar código ISO de 3 letras. Ejemplo: USD, COP");

        return normalized;
    }

    private static decimal RoundMoney(decimal amount)
    {
        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    private static string GenerateAccountNumber()
    {
        return DateTime.UtcNow.Ticks.ToString()[^10..];
    }

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private static object BuildAccount(Account account)
    {
        return new
        {
            account.Id,
            account.AccountNumber,
            account.OwnerId,
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

    private static object Error(string code, string description)
    {
        return new
        {
            success = false,
            code,
            description
        };
    }

    private sealed record TenantConfigCacheDto(
        Guid Id,
        decimal MaxTransactionAmount,
        FeeTypeEnum FeeType,
        decimal FeeValue);
}
