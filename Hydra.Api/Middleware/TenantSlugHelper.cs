namespace Hydra.Api.Middleware;

public static class TenantSlugHelper
{
    private static readonly HashSet<string> DefaultReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth", "accounts", "tenants", "health", "swagger"
    };

    public static bool IsReserved(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return true;

        var reserved = DefaultReserved;
        return reserved.Contains(slug);
    }
}
