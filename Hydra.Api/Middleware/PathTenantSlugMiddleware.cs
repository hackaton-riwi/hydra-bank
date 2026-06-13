using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hydra.Infrastructure.DATA;
using Hydra.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Hydra.Api.Middleware;

public class PathTenantSlugMiddleware
{
    private const string ApiPrefix = "/api/v1";
    private const string AuthSegment = "auth";
    private static readonly Regex AuthPathRegex = new(
        $"^{Regex.Escape(ApiPrefix)}/([^/]+)/{Regex.Escape(AuthSegment)}/(login|register)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly RequestDelegate _next;

    public PathTenantSlugMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, BankOsDbContext dbContext)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var match = AuthPathRegex.Match(path);

        if (match.Success)
        {
            var tenantSlug = match.Groups[1].Value;
            var action = match.Groups[2].Value.ToLowerInvariant();

            if (!TenantSlugHelper.IsReserved(tenantSlug))
            {
                var tenant = await dbContext.Tenants
                    .AsNoTracking()
                    .SingleOrDefaultAsync(t => t.Slug == tenantSlug);

                if (tenant is not null)
                {
                    context.Items["ResolvedTenantSlug"] = tenant.Slug;
                    context.Request.Path = $"{ApiPrefix}/{AuthSegment}/{action}";
                }
            }
        }

        await _next(context);
    }
}
