using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydra.Application.Interfaces;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Infrastructure.DATA;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Hydra.Application.Services;

public class IdempotencyService : IIdempotencyService
{
    private readonly IDatabase _redis;
    private readonly BankOsDbContext _db;
    private readonly TimeSpan _expiry = TimeSpan.FromHours(24);

    public IdempotencyService(IConnectionMultiplexer redis, BankOsDbContext db)
    {
        _redis = redis.GetDatabase();
        _db = db;
    }

    public async Task<IdempotencyResult?> GetAsync(
        Guid tenantId, Guid userId, string key)
    {
        try
        {
            var response = await _redis.StringGetAsync(ResponseKey(tenantId, userId, key));

            if (!response.IsNullOrEmpty)
            {
                return new IdempotencyResult
                {
                    StatusCode = 200,
                    ResponseBody = JsonSerializer.Deserialize<object>(response.ToString())!
                };
            }
        }
        catch (RedisException)
        {
        }

        var record = await _db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.UserId == userId &&
                r.IdempotencyKey == Guid.Parse(key) &&
                r.State == IdempotencyState.COMPLETED);

        if (record != null && record.ResponseBody != null)
        {
            try
            {
                await _redis.StringSetAsync(ResponseKey(tenantId, userId, key), record.ResponseBody, _expiry);
            }
            catch (RedisException)
            {
            }

            return new IdempotencyResult
            {
                StatusCode = record.StatusCode ?? 200,
                ResponseBody = JsonSerializer.Deserialize<object>(record.ResponseBody)!
            };
        }

        return null;
    }

    public async Task<bool> StartProcessingAsync(
        Guid tenantId, Guid userId, string key, string? requestBody)
    {
        var keyGuid = Guid.Parse(key);
        var now = DateTime.UtcNow;
        var requestHash = ComputeRequestHash(tenantId, userId, key, requestBody);

        var existing = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.UserId == userId &&
                r.IdempotencyKey == keyGuid);

        if (existing is not null)
        {
            if (existing.State == IdempotencyState.COMPLETED)
                return false;

            if (existing.State == IdempotencyState.PROCESSING && existing.ExpiresAt > now)
            {
                if (existing.RequestHash != requestHash)
                    throw new InvalidOperationException("Idempotency-Key reutilizada con payload diferente");

                return false;
            }

            _db.IdempotencyRecords.Remove(existing);
            await _db.SaveChangesAsync();
        }

        _db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            IdempotencyKey = keyGuid,
            RequestHash = requestHash,
            State = IdempotencyState.PROCESSING,
            CreatedAt = now,
            ExpiresAt = now.Add(_expiry)
        });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return false;
        }

        try
        {
            await _redis.StringSetAsync(
                ProcessingKey(tenantId, userId, key), "PROCESSING", _expiry, When.NotExists);
        }
        catch (RedisException)
        {
        }

        return true;
    }

    public async Task CompleteAsync(
        Guid tenantId, Guid userId, string key, object response)
    {
        var serialized = JsonSerializer.Serialize(response);
        var keyGuid = Guid.Parse(key);

        var record = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.UserId == userId &&
                r.IdempotencyKey == keyGuid);

        if (record == null)
        {
            record = new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                IdempotencyKey = keyGuid,
                RequestHash = ComputeRequestHash(tenantId, userId, key, null),
                ResponseBody = serialized,
                StatusCode = 200,
                State = IdempotencyState.COMPLETED,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_expiry)
            };
            _db.IdempotencyRecords.Add(record);
        }
        else
        {
            record.ResponseBody = serialized;
            record.StatusCode = 200;
            record.State = IdempotencyState.COMPLETED;
            record.ExpiresAt = DateTime.UtcNow.Add(_expiry);
        }

        await _db.SaveChangesAsync();

        try
        {
            await _redis.StringSetAsync(ResponseKey(tenantId, userId, key), serialized, _expiry);
            await _redis.KeyDeleteAsync(ProcessingKey(tenantId, userId, key));
        }
        catch (RedisException)
        {
        }
    }

    public async Task FailAsync(
        Guid tenantId, Guid userId, string key)
    {
        var record = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.UserId == userId &&
                r.IdempotencyKey == Guid.Parse(key));

        if (record != null)
        {
            record.State = IdempotencyState.FAILED;
            record.ExpiresAt = DateTime.UtcNow.Add(TimeSpan.FromHours(1));
            await _db.SaveChangesAsync();
        }

        try
        {
            await _redis.KeyDeleteAsync(ProcessingKey(tenantId, userId, key));
        }
        catch (RedisException)
        {
        }
    }

    private static string ProcessingKey(Guid tenantId, Guid userId, string key)
        => $"idempotency:{tenantId}:{userId}:{key}:processing";

    private static string ResponseKey(Guid tenantId, Guid userId, string key)
        => $"idempotency:{tenantId}:{userId}:{key}:response";

    private static string ComputeRequestHash(Guid tenantId, Guid userId, string key, string? requestBody)
    {
        var input = string.IsNullOrWhiteSpace(requestBody)
            ? $"{tenantId}:{userId}:{key}"
            : $"{tenantId}:{userId}:{key}:{requestBody}";

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }
}
