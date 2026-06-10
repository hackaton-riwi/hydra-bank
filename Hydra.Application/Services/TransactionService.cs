using Hydra.Application.DTOs;
using Hydra.Application.Interfaces;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Domain.Exceptions;
using Hydra.Infrastructure.DATA;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;

namespace Hydra.Application.Services;

public class TransactionService : ITransactionService
{
    private readonly BankOsDbContext _db;
    private readonly IIdempotencyService _idempotency;

    public TransactionService(
        BankOsDbContext db,
        IIdempotencyService idempotency)
    {
        _db = db;
        _idempotency = idempotency;
    }

    public async Task<TransferResponseDto> TransferAsync(
        Guid tenantId, Guid userId, TransferRequestDto request,
        string idempotencyKey, string correlationId)
    {
        var key = idempotencyKey;

        var acquired = await _idempotency.StartProcessingAsync(
            tenantId, userId, key);
        if (!acquired)
        {
            var cached = await _idempotency.GetAsync(tenantId, userId, key);
            if (cached is not null)
                return JsonSerializer.Deserialize<TransferResponseDto>(
                    JsonSerializer.Serialize(cached.ResponseBody))!;
            throw new TransactionInProgressException(key);
        }

        await using var tx = await _db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable);

        bool committed = false;

        try
        {
            var tenant = await _db.Tenants
                .FirstOrDefaultAsync(t => t.Id == tenantId)
                ?? throw new InvalidOperationException("Tenant no encontrado");

            var source = await _db.Accounts
                .FirstOrDefaultAsync(a =>
                    a.TenantId == tenantId && a.Id == request.SourceAccountId)
                ?? throw new InvalidOperationException(
                    "Cuenta origen no encontrada");

            var destination = await _db.Accounts
                .FirstOrDefaultAsync(a =>
                    a.TenantId == tenantId && a.Id == request.DestinationAccountId)
                ?? throw new InvalidOperationException(
                    "Cuenta destino no encontrada");

            if (source.Status != AccountStatus.ACTIVE)
                throw new InvalidOperationException(
                    "Cuenta origen no esta activa");

            if (destination.Status != AccountStatus.ACTIVE)
                throw new InvalidOperationException(
                    "Cuenta destino no esta activa");

            if (request.Amount > tenant.MaxTransactionAmount)
                throw new InvalidOperationException(
                    $"El monto excede el maximo permitido de {tenant.MaxTransactionAmount}");

            var fee = CalculateFee(tenant, request.Amount);
            var totalDebit = request.Amount + fee;

            if (source.Balance < totalDebit)
                throw new InvalidOperationException("Saldo insuficiente");

            decimal? convertedAmount = null;
            decimal? exchangeRate = null;

            if (source.Currency != destination.Currency)
            {
                var rate = await _db.ExchangeRates
                    .FirstOrDefaultAsync(r =>
                        r.TenantId == tenantId &&
                        r.FromCurrency == source.Currency &&
                        r.ToCurrency == destination.Currency)
                    ?? throw new InvalidOperationException(
                        $"No hay tasa de cambio para {source.Currency}->{destination.Currency}");

                exchangeRate = rate.Rate;
                convertedAmount = request.Amount * rate.Rate;
            }

            var now = DateTime.UtcNow;

            var transaction = new Transaction
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Type = TransactionType.TRANSFER,
                SourceAccountId = source.Id,
                DestinationAccountId = destination.Id,
                OriginalAmount = request.Amount,
                ConvertedAmount = convertedAmount,
                ExchangeRate = exchangeRate,
                FeeAmount = fee,
                Status = TransactionStatus.SUCCESS,
                IdempotencyKey = Guid.Parse(key),
                CorrelationId = Guid.Parse(correlationId),
                CreatedAt = now
            };

            source.Balance -= totalDebit;
            destination.Balance += convertedAmount ?? request.Amount;

            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Action = "TRANSFER",
                OldValue = JsonSerializer.Serialize(new
                {
                    SourceId = source.Id,
                    PreviousBalance = source.Balance + totalDebit
                }),
                NewValue = JsonSerializer.Serialize(new
                {
                    SourceId = source.Id,
                    SourceBalance = source.Balance,
                    DestinationId = destination.Id,
                    DestinationBalance = destination.Balance,
                    Fee = fee,
                    ConvertedAmount = convertedAmount
                }),
                CreatedAt = now
            };

            _db.Transactions.Add(transaction);
            _db.AuditLogs.Add(audit);
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            committed = true;

            var response = new TransferResponseDto
            {
                TransactionId = transaction.Id,
                Status = transaction.Status.ToString(),
                OriginalAmount = request.Amount,
                FeeAmount = fee,
                ConvertedAmount = convertedAmount,
                ExchangeRate = exchangeRate,
                CreatedAt = now
            };

            await _idempotency.CompleteAsync(
                tenantId, userId, key, response);

            return response;
        }
        catch
        {
            if (!committed)
                await tx.RollbackAsync();

            await _idempotency.FailAsync(tenantId, userId, key);
            throw;
        }
    }

    public async Task<DepositResponseDto> DepositAsync(
        Guid tenantId, Guid userId, DepositRequestDto request,
        string idempotencyKey, string correlationId)
    {
        var key = idempotencyKey;

        var acquired = await _idempotency.StartProcessingAsync(
            tenantId, userId, key);
        if (!acquired)
        {
            var cached = await _idempotency.GetAsync(tenantId, userId, key);
            if (cached is not null)
                return JsonSerializer.Deserialize<DepositResponseDto>(
                    JsonSerializer.Serialize(cached.ResponseBody))!;
            throw new TransactionInProgressException(key);
        }

        await using var tx = await _db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable);

        bool committed = false;

        try
        {
            var tenant = await _db.Tenants
                .FirstOrDefaultAsync(t => t.Id == tenantId)
                ?? throw new InvalidOperationException("Tenant no encontrado");

            var destination = await _db.Accounts
                .FirstOrDefaultAsync(a =>
                    a.TenantId == tenantId && a.Id == request.DestinationAccountId)
                ?? throw new InvalidOperationException(
                    "Cuenta destino no encontrada");

            if (destination.Status != AccountStatus.ACTIVE)
                throw new InvalidOperationException("Cuenta destino no esta activa");

            if (request.Amount > tenant.MaxTransactionAmount)
                throw new InvalidOperationException(
                    $"El monto excede el maximo permitido de {tenant.MaxTransactionAmount}");

            var fee = CalculateFee(tenant, request.Amount);
            var creditAmount = request.Amount - fee;

            var now = DateTime.UtcNow;

            var transaction = new Transaction
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Type = TransactionType.DEPOSIT,
                SourceAccountId = null,
                DestinationAccountId = destination.Id,
                OriginalAmount = request.Amount,
                ConvertedAmount = null,
                ExchangeRate = null,
                FeeAmount = fee,
                Status = TransactionStatus.SUCCESS,
                IdempotencyKey = Guid.Parse(key),
                CorrelationId = Guid.Parse(correlationId),
                CreatedAt = now
            };

            destination.Balance += creditAmount;

            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Action = "DEPOSIT",
                OldValue = JsonSerializer.Serialize(new
                {
                    DestinationId = destination.Id,
                    PreviousBalance = destination.Balance - creditAmount
                }),
                NewValue = JsonSerializer.Serialize(new
                {
                    DestinationId = destination.Id,
                    Balance = destination.Balance,
                    Fee = fee
                }),
                CreatedAt = now
            };

            _db.Transactions.Add(transaction);
            _db.AuditLogs.Add(audit);
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            committed = true;

            var response = new DepositResponseDto
            {
                TransactionId = transaction.Id,
                Status = transaction.Status.ToString(),
                OriginalAmount = request.Amount,
                FeeAmount = fee,
                CreatedAt = now
            };

            await _idempotency.CompleteAsync(
                tenantId, userId, key, response);

            return response;
        }
        catch
        {
            if (!committed)
                await tx.RollbackAsync();

            await _idempotency.FailAsync(tenantId, userId, key);
            throw;
        }
    }

    public async Task<WithdrawResponseDto> WithdrawAsync(
        Guid tenantId, Guid userId, WithdrawRequestDto request,
        string idempotencyKey, string correlationId)
    {
        var key = idempotencyKey;

        var acquired = await _idempotency.StartProcessingAsync(
            tenantId, userId, key);
        if (!acquired)
        {
            var cached = await _idempotency.GetAsync(tenantId, userId, key);
            if (cached is not null)
                return JsonSerializer.Deserialize<WithdrawResponseDto>(
                    JsonSerializer.Serialize(cached.ResponseBody))!;
            throw new TransactionInProgressException(key);
        }

        await using var tx = await _db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable);

        bool committed = false;

        try
        {
            var tenant = await _db.Tenants
                .FirstOrDefaultAsync(t => t.Id == tenantId)
                ?? throw new InvalidOperationException("Tenant no encontrado");

            var source = await _db.Accounts
                .FirstOrDefaultAsync(a =>
                    a.TenantId == tenantId && a.Id == request.SourceAccountId)
                ?? throw new InvalidOperationException(
                    "Cuenta origen no encontrada");

            if (source.Status != AccountStatus.ACTIVE)
                throw new InvalidOperationException("Cuenta origen no esta activa");

            if (request.Amount > tenant.MaxTransactionAmount)
                throw new InvalidOperationException(
                    $"El monto excede el maximo permitido de {tenant.MaxTransactionAmount}");

            var fee = CalculateFee(tenant, request.Amount);
            var totalDebit = request.Amount + fee;

            if (source.Balance < totalDebit)
                throw new InvalidOperationException("Saldo insuficiente");

            var now = DateTime.UtcNow;

            var transaction = new Transaction
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Type = TransactionType.WITHDRAW,
                SourceAccountId = source.Id,
                DestinationAccountId = null,
                OriginalAmount = request.Amount,
                ConvertedAmount = null,
                ExchangeRate = null,
                FeeAmount = fee,
                Status = TransactionStatus.SUCCESS,
                IdempotencyKey = Guid.Parse(key),
                CorrelationId = Guid.Parse(correlationId),
                CreatedAt = now
            };

            source.Balance -= totalDebit;

            var audit = new AuditLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Action = "WITHDRAW",
                OldValue = JsonSerializer.Serialize(new
                {
                    SourceId = source.Id,
                    PreviousBalance = source.Balance + totalDebit
                }),
                NewValue = JsonSerializer.Serialize(new
                {
                    SourceId = source.Id,
                    Balance = source.Balance,
                    Fee = fee
                }),
                CreatedAt = now
            };

            _db.Transactions.Add(transaction);
            _db.AuditLogs.Add(audit);
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            committed = true;

            var response = new WithdrawResponseDto
            {
                TransactionId = transaction.Id,
                Status = transaction.Status.ToString(),
                OriginalAmount = request.Amount,
                FeeAmount = fee,
                CreatedAt = now
            };

            await _idempotency.CompleteAsync(
                tenantId, userId, key, response);

            return response;
        }
        catch
        {
            if (!committed)
                await tx.RollbackAsync();

            await _idempotency.FailAsync(tenantId, userId, key);
            throw;
        }
    }

    private static decimal CalculateFee(Tenant tenant, decimal amount)
    {
        return tenant.FeeType switch
        {
            FeeTypeEnum.FIXED => tenant.FeeValue,
            FeeTypeEnum.PERCENTAGE => amount * tenant.FeeValue / 100m,
            _ => 0m
        };
    }
}
