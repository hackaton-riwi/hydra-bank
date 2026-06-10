namespace Hydra.Application.Caching;

public static class BankCacheKeys
{
    public static string TenantConfig(Guid tenantId) => $"bankos:tenant:{tenantId}:config";

    public static string ExchangeRate(Guid tenantId, string fromCurrency, string toCurrency)
    {
        return $"bankos:tenant:{tenantId}:exchange:{fromCurrency}:{toCurrency}";
    }
}
