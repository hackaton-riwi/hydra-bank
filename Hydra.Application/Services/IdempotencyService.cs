using Hydra.Application.Interfaces;
using StackExchange.Redis;
using System.Text.Json;

namespace Hydra.Application.Services;

public class IdempotencyService : IIdempotencyService
{
    private readonly IDatabase _redis;
    private readonly TimeSpan _expiry = TimeSpan.FromHours(24);

    public IdempotencyService(IConnectionMultiplexer redis)
    {
        _redis = redis.GetDatabase();
    }

    public async Task<IdempotencyResult?> GetAsync(
        Guid tenantId, Guid userId, string key)
    {
        var response = await _redis.StringGetAsync(ResponseKey(tenantId, userId, key));

        if (response.IsNullOrEmpty)
            return null;

        return new IdempotencyResult
        {
            StatusCode = 200,
            ResponseBody = JsonSerializer.Deserialize<object>(response.ToString())!
        };
    }
    
    
    
    

    public async Task<bool> StartProcessingAsync(
        Guid tenantId, Guid userId, string key)
    {
        if (await _redis.KeyExistsAsync(ResponseKey(tenantId, userId, key)))
            return false;

        return await _redis.StringSetAsync(
            ProcessingKey(tenantId, userId, key), "PROCESSING", _expiry, When.NotExists);
    }

    public async Task CompleteAsync(
        Guid tenantId, Guid userId, string key, object response)
    {
        var serialized = JsonSerializer.Serialize(response);

        await _redis.StringSetAsync(ResponseKey(tenantId, userId, key), serialized, _expiry);
        await _redis.KeyDeleteAsync(ProcessingKey(tenantId, userId, key));
    }

    public async Task FailAsync(
        Guid tenantId, Guid userId, string key)
    {
        await _redis.KeyDeleteAsync(ProcessingKey(tenantId, userId, key));
    }

    private static string ProcessingKey(Guid tenantId, Guid userId, string key)
        => $"idempotency:{tenantId}:{userId}:{key}:processing";

    private static string ResponseKey(Guid tenantId, Guid userId, string key)
        => $"idempotency:{tenantId}:{userId}:{key}:response";
}
