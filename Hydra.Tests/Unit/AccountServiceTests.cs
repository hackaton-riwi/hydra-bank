using Moq;
using Xunit;
using Hydra.Application.Services;
using Hydra.Application.Interfaces;
using Hydra.Application.DTOs;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Infrastructure.DATA;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Hydra.Tests.Unit;

public class AccountServiceTests
{
    private static BankOsDbContext CreateDbContext()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<BankOsDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new BankOsDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    private static ClaimsPrincipal CreateUserPrincipal(Guid tenantId, Guid userId, string role = "CLIENT")
    {
        var claims = new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("user_id", userId.ToString()),
            new Claim(ClaimTypes.Role, role),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static Mock<IHttpContextAccessor> CreateHttpContextAccessor(ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext { User = user };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);
        return accessor;
    }

    [Fact]
    public async Task DeactivateAsync_WhenAccountIsAlreadyInactive_ReturnsSuccessWithInactiveStatus()
    {
        var context = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        var user = new User
        {
            Id = userId,
            TenantId = tenantId,
            FullName = "Test User",
            DocumentNumber = "12345678",
            Email = "test@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        var account = new Account
        {
            Id = accountId,
            TenantId = tenantId,
            OwnerId = userId,
            AccountNumber = "1234567890",
            Balance = 100000m,
            Status = AccountStatus.INACTIVE,
            Currency = "COP",
            DeactivatedAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };

        context.BankUsers.Add(user);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var mockTransactionService = new Mock<ITransactionService>();
        var mockAccessor = CreateHttpContextAccessor(CreateUserPrincipal(tenantId, userId));
        
        var service = new AccountService(
            context,
            mockAccessor.Object,
            mockTransactionService.Object
        );

        var result = await service.DeactivateAsync($"ACC-{account.Id.ToString("N")[..8].ToUpperInvariant()}");

        Assert.NotNull(result);
        var resultObj = result as dynamic;
        Assert.True(resultObj.success);
        Assert.Equal("ACCOUNT_ALREADY_INACTIVE", resultObj.code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(-0.01)]
    public async Task RechargeAsync_WithInvalidAmount_ThrowsException(decimal invalidAmount)
    {
        var context = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var user = new User
        {
            Id = userId,
            TenantId = tenantId,
            FullName = "Test User",
            DocumentNumber = "12345678",
            Email = "test@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        var account = new Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OwnerId = userId,
            AccountNumber = "1234567890",
            Balance = 50000m,
            Status = AccountStatus.ACTIVE,
            Currency = "COP",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.BankUsers.Add(user);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var mockTransactionService = new Mock<ITransactionService>();
        var mockAccessor = CreateHttpContextAccessor(CreateUserPrincipal(tenantId, userId));
        
        var service = new AccountService(
            context,
            mockAccessor.Object,
            mockTransactionService.Object
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RechargeAsync(new RechargeAccountDto { Amount = invalidAmount }));
    }

    [Fact]
    public async Task DeactivateAsync_WhenAccountBelongsToAnotherUser_ThrowsException()
    {
        var context = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userIdOwner = Guid.NewGuid();
        var userIdAttacker = Guid.NewGuid();

        var owner = new User
        {
            Id = userIdOwner,
            TenantId = tenantId,
            FullName = "Owner",
            DocumentNumber = "11111111",
            Email = "owner@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        var attacker = new User
        {
            Id = userIdAttacker,
            TenantId = tenantId,
            FullName = "Attacker",
            DocumentNumber = "22222222",
            Email = "attacker@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        var account = new Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OwnerId = userIdOwner,
            AccountNumber = "1234567890",
            Balance = 100000m,
            Status = AccountStatus.ACTIVE,
            Currency = "COP",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.BankUsers.AddRange(owner, attacker);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var mockTransactionService = new Mock<ITransactionService>();
        var mockAccessor = CreateHttpContextAccessor(CreateUserPrincipal(tenantId, userIdAttacker));
        
        var service = new AccountService(
            context,
            mockAccessor.Object,
            mockTransactionService.Object
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeactivateAsync($"ACC-{account.Id.ToString("N")[..8].ToUpperInvariant()}"));
    }

    [Fact]
    public async Task DeactivateAsync_WhenAccountIsActive_DeactivatesSuccessfully()
    {
        var context = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var user = new User
        {
            Id = userId,
            TenantId = tenantId,
            FullName = "Test User",
            DocumentNumber = "12345678",
            Email = "test@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        var account = new Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OwnerId = userId,
            AccountNumber = "1234567890",
            Balance = 100000m,
            Status = AccountStatus.ACTIVE,
            Currency = "COP",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.BankUsers.Add(user);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var mockTransactionService = new Mock<ITransactionService>();
        var mockAccessor = CreateHttpContextAccessor(CreateUserPrincipal(tenantId, userId));
        
        var service = new AccountService(
            context,
            mockAccessor.Object,
            mockTransactionService.Object
        );

        var result = await service.DeactivateAsync($"ACC-{account.Id.ToString("N")[..8].ToUpperInvariant()}");

        Assert.NotNull(result);
        var resultObj = result as dynamic;
        Assert.True(resultObj.success);
        Assert.Equal("ACCOUNT_DEACTIVATED", resultObj.code);
        Assert.Equal("INACTIVE", resultObj.data.Status);
    }

    [Fact]
    public async Task GetMyAccountAsync_WhenNoAccountExists_ThrowsException()
    {
        var context = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var user = new User
        {
            Id = userId,
            TenantId = tenantId,
            FullName = "Test User",
            DocumentNumber = "12345678",
            Email = "test@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        context.BankUsers.Add(user);
        await context.SaveChangesAsync();

        var mockTransactionService = new Mock<ITransactionService>();
        var mockAccessor = CreateHttpContextAccessor(CreateUserPrincipal(tenantId, userId));
        
        var service = new AccountService(
            context,
            mockAccessor.Object,
            mockTransactionService.Object
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetMyAccountAsync());
    }

    [Fact]
    public async Task DeactivateAsync_WithInvalidAccountKey_ThrowsException()
    {
        var context = CreateDbContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var user = new User
        {
            Id = userId,
            TenantId = tenantId,
            FullName = "Test User",
            DocumentNumber = "12345678",
            Email = "test@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        context.BankUsers.Add(user);
        await context.SaveChangesAsync();

        var mockTransactionService = new Mock<ITransactionService>();
        var mockAccessor = CreateHttpContextAccessor(CreateUserPrincipal(tenantId, userId));
        
        var service = new AccountService(
            context,
            mockAccessor.Object,
            mockTransactionService.Object
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeactivateAsync("INVALID-KEY-123"));
    }
}
