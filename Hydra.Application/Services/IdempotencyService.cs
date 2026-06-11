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
        // Try Redis first for fast path
        var response = await _redis.StringGetAsync(ResponseKey(tenantId, userId, key));

        if (!response.IsNullOrEmpty)
        {
            return new IdempotencyResult
            {
                StatusCode = 200,
                ResponseBody = JsonSerializer.Deserialize<object>(response.ToString())!
            };
        }

        // Fallback to DB
        var record = await _db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.UserId == userId &&
                r.IdempotencyKey == Guid.Parse(key) &&
                r.State == IdempotencyState.COMPLETED);

        if (record != null && record.ResponseBody != null)
        {
            // Cache in Redis for next time
            await _redis.StringSetAsync(ResponseKey(tenantId, userId, key), record.ResponseBody, _expiry);

            return new IdempotencyResult
            {
                StatusCode = record.StatusCode ?? 200,
                ResponseBody = JsonSerializer.Deserialize<object>(record.ResponseBody)!
            };
        }

        return null;
    }
    
    
    
    

    public async Task<bool> StartProcessingAsync(
        Guid tenantId, Guid userId, string key)
    {
        // Check if already completed in Redis
        if (await _redis.KeyExistsAsync(ResponseKey(tenantId, userId, key)))
            return false;

        // Try atomic lock acquisition
        var acquired = await _redis.StringSetAsync(
            ProcessingKey(tenantId, userId, key), "PROCESSING", _expiry, When.NotExists);

        if (!acquired)
        {
            // Check DB for existing record (could be PROCESSING or COMPLETED)
            var existing = await _db.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r =>
                    r.TenantId == tenantId &&
                    r.UserId == userId &&
                    r.IdempotencyKey == Guid.Parse(key));

            if (existing != null)
            {
                if (existing.State == IdempotencyState.COMPLETED)
                    return false; // Already completed, GetAsync will handle replay
                if (existing.State == IdempotencyState.PROCESSING)
                    return false; // Still processing
                if (existing.State == IdempotencyState.FAILED)
                {
                    // Allow retry for failed - delete and allow new attempt
                    _db.IdempotencyRecords.Remove(existing);
                    await _db.SaveChangesAsync();
                    // Retry lock acquisition
                    return await _redis.StringSetAsync(
                        ProcessingKey(tenantId, userId, key), "PROCESSING", _expiry, When.NotExists);
                }
            }
        }

        return acquired;
    }

    public async Task CompleteAsync(
        Guid tenantId, Guid userId, string key, object response)
    {
        var serialized = JsonSerializer.Serialize(response);
        var requestHash = ComputeRequestHash(tenantId, userId, key);

        // Persist to DB
        var record = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.UserId == userId &&
                r.IdempotencyKey == Guid.Parse(key));

        if (record == null)
        {
            record = new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                IdempotencyKey = Guid.Parse(key),
                RequestHash = requestHash,
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
            // Validate request hash hasn't changed
            if (record.RequestHash != requestHash)
            {
                throw new InvalidOperationException("Idempotency key reused with different payload");
            }
            record.ResponseBody = serialized;
            record.StatusCode = 200;
            record.State = IdempotencyState.COMPLETED;
            record.ExpiresAt = DateTime.UtcNow.Add(_expiry);
        }

        await _db.SaveChangesAsync();

        // Cache in Redis
        await _redis.StringSetAsync(ResponseKey(tenantId, userId, key), serialized, _expiry);
        await _redis.KeyDeleteAsync(ProcessingKey(tenantId, userId, key));
    }

    public async Task FailAsync(
        Guid tenantId, Guid userId, string key)
    {
        var requestHash = ComputeRequestHash(tenantId, userId, key);

        // Update DB record
        var record = await _db.IdempotencyRecords
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.UserId == userId &&
                r.IdempotencyKey == Guid.Parse(key));

        if (record != null)
        {
            if (record.RequestHash != requestHash)
            {
                throw new InvalidOperationException("Idempotency key reused with different payload");
            }
            record.State = IdempotencyState.FAILED;
            record.ExpiresAt = DateTime.UtcNow.Add(TimeSpan.FromHours(1));
            await _db.SaveChangesAsync();
        }

        // Clean up Redis
        await _redis.KeyDeleteAsync(ProcessingKey(tenantId, userId, key));
    }

    private static string ProcessingKey(Guid tenantId, Guid userId, string key)
        => $"idempotency:{tenantId}:{userId}:{key}:processing";

    private static string ResponseKey(Guid tenantId, Guid userId, string key)
        => $"idempotency:{tenantId}:{userId}:{key}:response";

    private static string ComputeRequestHash(Guid tenantId, Guid userId, string key)
    {
        // In production, this should hash the actual request body
        // For now, use a deterministic key-based hash
        var input = $"{tenantId}:{userId}:{key}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }
}