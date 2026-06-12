namespace Hydra.Application.Interfaces;

public interface IIdempotencyService
{
    Task<IdempotencyResult?> GetAsync(
        Guid tenantId, Guid userId, string key);

    Task<bool> StartProcessingAsync(
        Guid tenantId, Guid userId, string key, string? requestBody);

    Task CompleteAsync(
        Guid tenantId, Guid userId, string key, object response);

    Task FailAsync(
        Guid tenantId, Guid userId, string key);
}

public class IdempotencyResult
{
    public int StatusCode { get; set; }
    public object ResponseBody { get; set; } = null!;
}
