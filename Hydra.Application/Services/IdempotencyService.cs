using Hydra.Application.Interfaces;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Infrastructure.DATA;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.Json;

namespace Hydra.Application.Services;

public class IdempotencyService : IIdempotencyService
{
    private readonly IDatabase _redis;
    private readonly BankOsDbContext _db;
    private readonly TimeSpan _expiry = TimeSpan.FromHours(24);

    public IdempotencyService(
        IConnectionMultiplexer redis,
        BankOsDbContext db)
    {
        _redis = redis.GetDatabase();
        _db = db;
    }

    public async Task<IdempotencyResult?> GetAsync(
        Guid tenantId, Guid userId, string key)
    {
        var record = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.UserId == userId &&
                r.IdempotencyKey.ToString() == key &&
                r.State == IdempotencyState.COMPLETED);

        if (record is null)
            return null;

        return new IdempotencyResult
        {
            StatusCode = record.StatusCode ?? 200,
            ResponseBody = record.ResponseBody is not null
                ? JsonSerializer.Deserialize<object>(record.ResponseBody)!
                : new { message = "Operacion completada exitosamente" }
        };
    }

    public async Task<bool> StartProcessingAsync(
        Guid tenantId, Guid userId, string key)
    {
        var redisKey = $"idempotency:{tenantId}:{userId}:{key}";
        return await _redis.StringSetAsync(
            redisKey, "PROCESSING", _expiry, When.NotExists);
    }

    public async Task CompleteAsync(
        Guid tenantId, Guid userId, string key, object response)
    {
        var redisKey = $"idempotency:{tenantId}:{userId}:{key}";
        var serialized = JsonSerializer.Serialize(response);

        var record = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.UserId == userId &&
                r.IdempotencyKey.ToString() == key);

        if (record is null)
        {
            record = new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                IdempotencyKey = Guid.Parse(key),
                RequestHash = $"{tenantId}:{userId}:{key}",
                CreatedAt = DateTime.UtcNow
            };
            _db.IdempotencyRecords.Add(record);
        }

        record.ResponseBody = serialized;
        record.StatusCode = 200;
        record.State = IdempotencyState.COMPLETED;
        record.ExpiresAt = DateTime.UtcNow.Add(_expiry);

        await _db.SaveChangesAsync();

        await _redis.StringSetAsync(
            redisKey + ":response", serialized, _expiry);
        await _redis.KeyDeleteAsync(redisKey);
    }

    public async Task FailAsync(
        Guid tenantId, Guid userId, string key)
    {
        var redisKey = $"idempotency:{tenantId}:{userId}:{key}";
        await _redis.KeyDeleteAsync(redisKey);

        var record = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.UserId == userId &&
                r.IdempotencyKey.ToString() == key);

        if (record is null)
        {
            record = new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                IdempotencyKey = Guid.Parse(key),
                RequestHash = $"{tenantId}:{userId}:{key}",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_expiry),
                State = IdempotencyState.FAILED
            };
            _db.IdempotencyRecords.Add(record);
        }
        else
        {
            record.State = IdempotencyState.FAILED;
        }

        await _db.SaveChangesAsync();
    }
}
