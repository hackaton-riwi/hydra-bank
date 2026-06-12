using System.Data;
using System.Text.Json;
using Hydra.Application.DTOs;
using Hydra.Application.Interfaces;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Domain.Exceptions;
using Hydra.Infrastructure.DATA;
using Microsoft.EntityFrameworkCore;

namespace Hydra.Application.Services;

public class TransactionService : ITransactionService
{
    private readonly BankOsDbContext _db;
    private readonly IIdempotencyService _idempotency;
    private readonly IWebhookNotifier _webhook;

    public TransactionService(BankOsDbContext db, IIdempotencyService idempotency, IWebhookNotifier webhook)
    {
        _db = db;
        _idempotency = idempotency;
        _webhook = webhook;
    }

    public async Task<TransferResponseDto> TransferAsync(
        Guid tenantId,
        Guid userId,
        TransferRequestDto request,
        string idempotencyKey,
        string correlationId)
    {
        var requestBody = JsonSerializer.Serialize(request);
        var acquired = await _idempotency.StartProcessingAsync(tenantId, userId, idempotencyKey, requestBody);
        if (!acquired)
        {
            var cached = await _idempotency.GetAsync(tenantId, userId, idempotencyKey);
            if (cached is not null)
            {
                return JsonSerializer.Deserialize<TransferResponseDto>(
                    JsonSerializer.Serialize(cached.ResponseBody))!;
            }

            throw new TransactionInProgressException(idempotencyKey);
        }

        await using var dbTransaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var committed = false;

        try
        {
            if (request.Amount <= 0)
                throw new InvalidOperationException("El monto debe ser mayor que cero");

            var destinationDocument = NormalizeDocumentNumber(request.DestinationDocumentNumber);
            if (string.IsNullOrWhiteSpace(destinationDocument))
                throw new InvalidOperationException("El documento destino es obligatorio");

            var tenant = await _db.Tenants
                .SingleOrDefaultAsync(x => x.Id == tenantId)
                ?? throw new InvalidOperationException("Tenant no encontrado");

            if (request.Amount > tenant.MaxTransactionAmount)
                throw new InvalidOperationException($"El monto excede el maximo permitido de {tenant.MaxTransactionAmount}");

            var source = await _db.Accounts
                .Include(x => x.User)
                .SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.OwnerId == userId)
                ?? throw new InvalidOperationException("El usuario autenticado no tiene cuenta origen");

            var destinationUser = await _db.BankUsers
                .AsNoTracking()
                .SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.DocumentNumber == destinationDocument &&
                    x.Role == UserRole.CLIENT)
                ?? throw new InvalidOperationException("No existe un cliente destino con ese documento");

            if (destinationUser.Id == userId)
                throw new InvalidOperationException("No puedes transferirte a tu propia cuenta");

            var destination = await _db.Accounts
                .Include(x => x.User)
                .SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.OwnerId == destinationUser.Id)
                ?? throw new InvalidOperationException("El cliente destino no tiene cuenta activa en el banco");

            if (source.Status != AccountStatus.ACTIVE)
                throw new InvalidOperationException("Cuenta origen no esta activa");

            if (destination.Status != AccountStatus.ACTIVE)
                throw new InvalidOperationException("Cuenta destino no esta activa");

            var fee = RoundMoney(CalculateFee(tenant, request.Amount));
            var totalDebit = RoundMoney(request.Amount + fee);

            if (source.Balance < totalDebit)
                throw new InvalidOperationException("Saldo insuficiente");

            if (Math.Round(source.Balance - totalDebit, 2) < 0)
                throw new InvalidOperationException("Saldo insuficiente");

            var now = DateTime.UtcNow;

            source.Balance = RoundMoney(source.Balance - totalDebit);
            source.UpdatedAt = now;
            destination.Balance = RoundMoney(destination.Balance + request.Amount);
            destination.UpdatedAt = now;

            var transaction = new Transaction
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Type = TransactionType.TRANSFER,
                SourceAccountId = source.Id,
                DestinationAccountId = destination.Id,
                OriginalAmount = RoundMoney(request.Amount),
                FeeAmount = fee,
                Status = TransactionStatus.SUCCESS,
                CorrelationId = Guid.Parse(correlationId),
                CreatedAt = now
            };

            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Action = "TRANSFER",
                OldValue = JsonSerializer.Serialize(new
                {
                    SourceAccountId = source.Id,
                    DestinationAccountId = destination.Id,
                    SourcePreviousBalance = source.Balance + totalDebit,
                    DestinationPreviousBalance = destination.Balance - request.Amount
                }),
                NewValue = JsonSerializer.Serialize(new
                {
                    SourceAccountId = source.Id,
                    DestinationAccountId = destination.Id,
                    SourceBalance = source.Balance,
                    DestinationBalance = destination.Balance,
                    Amount = request.Amount,
                    Fee = fee
                }),
                CreatedAt = now
            };

            _db.Transactions.Add(transaction);
            _db.AuditLogs.Add(audit);
            await _db.SaveChangesAsync();
            await dbTransaction.CommitAsync();
            committed = true;

            var response = new TransferResponseDto
            {
                TransactionId = transaction.Id,
                TransactionShortId = BuildShortId("TRX", transaction.Id),
                Status = transaction.Status.ToString(),
                SourceAccountId = source.Id,
                SourceAccountShortId = BuildShortId("ACC", source.Id),
                DestinationAccountId = destination.Id,
                DestinationAccountShortId = BuildShortId("ACC", destination.Id),
                DestinationDocumentNumber = destination.User.DocumentNumber,
                Amount = request.Amount,
                FeeAmount = fee,
                SourceBalance = source.Balance,
                DestinationBalance = destination.Balance,
                CreatedAt = now
            };

            await _idempotency.CompleteAsync(tenantId, userId, idempotencyKey, response);

            // Fire-and-forget webhook notification
            var tenantEntity = await _db.Tenants
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == tenantId);
            
            if (tenantEntity?.WebhookUrl != null)
            {
                var webhookPayload = new WebhookTransactionPayload
                {
                    TransactionId = transaction.Id,
                    TenantId = tenantId,
                    UserId = userId,
                    TransactionType = "TRANSFER",
                    Amount = request.Amount,
                    FeeAmount = fee,
                    Status = "SUCCESS",
                    CreatedAt = now,
                    SourceAccountId = source.Id,
                    DestinationAccountId = destination.Id,
                    CorrelationId = correlationId
                };
                await _webhook.NotifyAsync(tenantEntity.WebhookUrl, webhookPayload);
            }

            return response;
        }
        catch
        {
            if (!committed)
                await RollbackIfPossibleAsync(dbTransaction);

            await MarkIdempotencyFailedIfPossibleAsync(tenantId, userId, idempotencyKey);
            throw;
        }
    }

    public async Task<DepositResponseDto> DepositAsync(
        Guid tenantId,
        Guid userId,
        DepositRequestDto request,
        string idempotencyKey,
        string correlationId)
    {
        var requestBody = JsonSerializer.Serialize(request);
        var acquired = await _idempotency.StartProcessingAsync(tenantId, userId, idempotencyKey, requestBody);
        if (!acquired)
        {
            var cached = await _idempotency.GetAsync(tenantId, userId, idempotencyKey);
            if (cached is not null)
            {
                return JsonSerializer.Deserialize<DepositResponseDto>(
                    JsonSerializer.Serialize(cached.ResponseBody))!;
            }

            throw new TransactionInProgressException(idempotencyKey);
        }

        await using var dbTransaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var committed = false;

        try
        {
            if (request.Amount <= 0)
                throw new InvalidOperationException("El monto debe ser mayor que cero");

            var tenant = await _db.Tenants
                .SingleOrDefaultAsync(x => x.Id == tenantId)
                ?? throw new InvalidOperationException("Tenant no encontrado");

            if (request.Amount > tenant.MaxTransactionAmount)
                throw new InvalidOperationException($"El monto excede el maximo permitido de {tenant.MaxTransactionAmount}");

            var destinationAccountId = await ResolveAccountIdAsync(tenantId, request.DestinationAccountId);
            var account = await _db.Accounts
                .SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.Id == destinationAccountId)
                ?? throw new InvalidOperationException("Cuenta destino no encontrada");

            if (account.Status != AccountStatus.ACTIVE)
                throw new InvalidOperationException("Cuenta destino no esta activa");

            var fee = RoundMoney(CalculateFee(tenant, request.Amount));
            var netAmount = RoundMoney(request.Amount - fee);

            if (netAmount <= 0)
                throw new InvalidOperationException("El monto neto despues de comision debe ser mayor que cero");

            var now = DateTime.UtcNow;

            account.Balance = RoundMoney(account.Balance + netAmount);
            account.UpdatedAt = now;

            var transaction = new Transaction
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Type = TransactionType.DEPOSIT,
                SourceAccountId = null,
                DestinationAccountId = account.Id,
                OriginalAmount = RoundMoney(request.Amount),
                FeeAmount = fee,
                Status = TransactionStatus.SUCCESS,
                CorrelationId = Guid.Parse(correlationId),
                CreatedAt = now
            };

            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Action = "DEPOSIT",
                OldValue = JsonSerializer.Serialize(new
                {
                    DestinationAccountId = account.Id,
                    PreviousBalance = account.Balance - netAmount
                }),
                NewValue = JsonSerializer.Serialize(new
                {
                    DestinationAccountId = account.Id,
                    Balance = account.Balance,
                    Amount = request.Amount,
                    Fee = fee,
                    NetAmount = netAmount
                }),
                CreatedAt = now
            };

            _db.Transactions.Add(transaction);
            _db.AuditLogs.Add(audit);
            await _db.SaveChangesAsync();
            await dbTransaction.CommitAsync();
            committed = true;

            var response = new DepositResponseDto
            {
                TransactionId = transaction.Id,
                TransactionShortId = BuildShortId("TRX", transaction.Id),
                Status = transaction.Status.ToString(),
                DestinationAccountShortId = BuildShortId("ACC", account.Id),
                DestinationAccountInternalId = account.Id,
                OriginalAmount = request.Amount,
                FeeAmount = fee,
                NetAmount = netAmount,
                DestinationBalance = account.Balance,
                CreatedAt = now
            };

            await _idempotency.CompleteAsync(tenantId, userId, idempotencyKey, response);

            var tenantEntity = await _db.Tenants
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == tenantId);

            if (tenantEntity?.WebhookUrl != null)
            {
                var webhookPayload = new WebhookTransactionPayload
                {
                    TransactionId = transaction.Id,
                    TenantId = tenantId,
                    UserId = userId,
                    TransactionType = "DEPOSIT",
                    Amount = request.Amount,
                    FeeAmount = fee,
                    Status = "SUCCESS",
                    CreatedAt = now,
                    DestinationAccountId = account.Id,
                    CorrelationId = correlationId
                };
                await _webhook.NotifyAsync(tenantEntity.WebhookUrl, webhookPayload);
            }

            return response;
        }
        catch
        {
            if (!committed)
                await RollbackIfPossibleAsync(dbTransaction);

            await MarkIdempotencyFailedIfPossibleAsync(tenantId, userId, idempotencyKey);
            throw;
        }
    }

    public async Task<WithdrawResponseDto> WithdrawAsync(
        Guid tenantId,
        Guid userId,
        WithdrawRequestDto request,
        string idempotencyKey,
        string correlationId)
    {
        var requestBody = JsonSerializer.Serialize(request);
        var acquired = await _idempotency.StartProcessingAsync(tenantId, userId, idempotencyKey, requestBody);
        if (!acquired)
        {
            var cached = await _idempotency.GetAsync(tenantId, userId, idempotencyKey);
            if (cached is not null)
            {
                return JsonSerializer.Deserialize<WithdrawResponseDto>(
                    JsonSerializer.Serialize(cached.ResponseBody))!;
            }

            throw new TransactionInProgressException(idempotencyKey);
        }

        await using var dbTransaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var committed = false;

        try
        {
            if (request.Amount <= 0)
                throw new InvalidOperationException("El monto debe ser mayor que cero");

            var tenant = await _db.Tenants
                .SingleOrDefaultAsync(x => x.Id == tenantId)
                ?? throw new InvalidOperationException("Tenant no encontrado");

            if (request.Amount > tenant.MaxTransactionAmount)
                throw new InvalidOperationException($"El monto excede el maximo permitido de {tenant.MaxTransactionAmount}");

            var sourceAccountId = await ResolveAccountIdAsync(tenantId, request.SourceAccountId);
            var account = await _db.Accounts
                .SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.Id == sourceAccountId &&
                    x.OwnerId == userId)
                ?? throw new InvalidOperationException("Cuenta origen no encontrada");

            if (account.Status != AccountStatus.ACTIVE)
                throw new InvalidOperationException("Cuenta origen no esta activa");

            var fee = RoundMoney(CalculateFee(tenant, request.Amount));
            var totalDebit = RoundMoney(request.Amount + fee);

            if (account.Balance < totalDebit)
                throw new InvalidOperationException("Saldo insuficiente");

            if (Math.Round(account.Balance - totalDebit, 2) < 0)
                throw new InvalidOperationException("Saldo insuficiente");

            var now = DateTime.UtcNow;

            account.Balance = RoundMoney(account.Balance - totalDebit);
            account.UpdatedAt = now;

            var transaction = new Transaction
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Type = TransactionType.WITHDRAW,
                SourceAccountId = account.Id,
                DestinationAccountId = null,
                OriginalAmount = RoundMoney(request.Amount),
                FeeAmount = fee,
                Status = TransactionStatus.SUCCESS,
                CorrelationId = Guid.Parse(correlationId),
                CreatedAt = now
            };

            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Action = "WITHDRAW",
                OldValue = JsonSerializer.Serialize(new
                {
                    SourceAccountId = account.Id,
                    PreviousBalance = account.Balance + totalDebit
                }),
                NewValue = JsonSerializer.Serialize(new
                {
                    SourceAccountId = account.Id,
                    Balance = account.Balance,
                    Amount = request.Amount,
                    Fee = fee,
                    TotalDebit = totalDebit
                }),
                CreatedAt = now
            };

            _db.Transactions.Add(transaction);
            _db.AuditLogs.Add(audit);
            await _db.SaveChangesAsync();
            await dbTransaction.CommitAsync();
            committed = true;

            var response = new WithdrawResponseDto
            {
                TransactionId = transaction.Id,
                TransactionShortId = BuildShortId("TRX", transaction.Id),
                Status = transaction.Status.ToString(),
                SourceAccountShortId = BuildShortId("ACC", account.Id),
                SourceAccountInternalId = account.Id,
                OriginalAmount = request.Amount,
                FeeAmount = fee,
                TotalDebit = totalDebit,
                SourceBalance = account.Balance,
                CreatedAt = now
            };

            await _idempotency.CompleteAsync(tenantId, userId, idempotencyKey, response);

            var tenantEntity = await _db.Tenants
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == tenantId);

            if (tenantEntity?.WebhookUrl != null)
            {
                var webhookPayload = new WebhookTransactionPayload
                {
                    TransactionId = transaction.Id,
                    TenantId = tenantId,
                    UserId = userId,
                    TransactionType = "WITHDRAW",
                    Amount = request.Amount,
                    FeeAmount = fee,
                    Status = "SUCCESS",
                    CreatedAt = now,
                    SourceAccountId = account.Id,
                    CorrelationId = correlationId
                };
                await _webhook.NotifyAsync(tenantEntity.WebhookUrl, webhookPayload);
            }

            return response;
        }
        catch
        {
            if (!committed)
                await RollbackIfPossibleAsync(dbTransaction);

            await MarkIdempotencyFailedIfPossibleAsync(tenantId, userId, idempotencyKey);
            throw;
        }
    }

    private static decimal CalculateFee(Tenant tenant, decimal amount)
    {
        var fee = tenant.FeeType switch
        {
            FeeTypeEnum.FIXED => tenant.FeeValue,
            FeeTypeEnum.PERCENTAGE => amount * tenant.FeeValue / 100m,
            _ => 0m
        };

        return fee;
    }

    private static decimal RoundMoney(decimal amount)
    {
        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    private static string NormalizeDocumentNumber(string documentNumber)
    {
        return documentNumber.Trim().ToUpperInvariant();
    }

    private async Task<Guid> ResolveAccountIdAsync(Guid tenantId, string accountKey)
    {
        var normalized = accountKey.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("La cuenta es obligatoria");

        if (Guid.TryParse(normalized, out var accountId))
            return accountId;

        var accountByNumber = await _db.Accounts
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
            throw new InvalidOperationException("La cuenta debe ser un numero de cuenta, GUID o codigo corto tipo ACC-1234ABCD");

        var accounts = await _db.Accounts
            .AsNoTracking()
            .Where(account => account.TenantId == tenantId)
            .Select(account => account.Id)
            .ToListAsync();

        var matches = accounts
            .Where(id => id.ToString("N").StartsWith(shortCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("Cuenta no encontrada"),
            _ => throw new InvalidOperationException("El codigo corto de cuenta es ambiguo; usa mas caracteres del GUID")
        };
    }

    private static string BuildShortId(string prefix, Guid id)
    {
        return $"{prefix}-{id.ToString("N")[..8].ToUpperInvariant()}";
    }

    private static async Task RollbackIfPossibleAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction dbTransaction)
    {
        try
        {
            await dbTransaction.RollbackAsync();
        }
        catch
        {
        }
    }

    private async Task MarkIdempotencyFailedIfPossibleAsync(Guid tenantId, Guid userId, string idempotencyKey)
    {
        try
        {
            await _idempotency.FailAsync(tenantId, userId, idempotencyKey);
        }
        catch
        {
        }
    }
}
