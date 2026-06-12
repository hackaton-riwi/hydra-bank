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

public class SecurityTests
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

    [Fact]
    public void TenantIsolation_UsersFromDifferentTenantsCannotAccessEachOthersAccounts()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var context = CreateDbContext();

        var userInTenantA = new User
        {
            Id = userA,
            TenantId = tenantA,
            FullName = "User A",
            DocumentNumber = "DOC-A",
            Email = "a@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        var userInTenantB = new User
        {
            Id = userB,
            TenantId = tenantB,
            FullName = "User B",
            DocumentNumber = "DOC-B",
            Email = "b@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        var accountA = new Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            OwnerId = userA,
            AccountNumber = "1000000001",
            Balance = 500000m,
            Status = AccountStatus.ACTIVE,
            Currency = "COP",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.BankUsers.AddRange(userInTenantA, userInTenantB);
        context.Accounts.Add(accountA);
        context.SaveChanges();

        var accountsInTenantA = context.Accounts
            .Where(a => a.TenantId == tenantA)
            .ToList();

        var accountsInTenantB = context.Accounts
            .Where(a => a.TenantId == tenantB)
            .ToList();

        Assert.Single(accountsInTenantA);
        Assert.Empty(accountsInTenantB);
    }

    [Fact]
    public void CrossTenantTransfer_ShouldBeBlockedByTenantScoping()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var differentTenantId = Guid.NewGuid();

        var context = CreateDbContext();

        var sourceUser = new User
        {
            Id = userId,
            TenantId = tenantId,
            FullName = "Source",
            DocumentNumber = "SRC-DOC",
            Email = "src@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        var destinationUser = new User
        {
            Id = Guid.NewGuid(),
            TenantId = differentTenantId,
            FullName = "Destination",
            DocumentNumber = "DST-DOC",
            Email = "dst@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash"
        };

        var sourceAccount = new Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OwnerId = userId,
            AccountNumber = "2000000001",
            Balance = 100000m,
            Status = AccountStatus.ACTIVE,
            Currency = "COP",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.BankUsers.AddRange(sourceUser, destinationUser);
        context.Accounts.Add(sourceAccount);
        context.SaveChanges();

        var destinationAccountInSourceTenant = context.Accounts
            .SingleOrDefault(a => a.TenantId == tenantId && a.OwnerId == destinationUser.Id);

        Assert.Null(destinationAccountInSourceTenant);
    }

    [Fact]
    public async Task CreateAsync_DuplicateAccount_ThrowsException()
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

        var existingAccount = new Account
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
        context.Accounts.Add(existingAccount);
        await context.SaveChangesAsync();

        var httpContext = new DefaultHttpContext
        {
            User = CreateUserPrincipal(tenantId, userId)
        };
        var mockAccessor = new Mock<IHttpContextAccessor>();
        mockAccessor.Setup(x => x.HttpContext).Returns(httpContext);

        var mockTransactionService = new Mock<ITransactionService>();
        var service = new AccountService(context, mockAccessor.Object, mockTransactionService.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new CreateAccountDto()));
    }

    private static ClaimsPrincipal CreateUserPrincipal(Guid tenantId, Guid userId)
    {
        var claims = new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("user_id", userId.ToString()),
            new Claim(ClaimTypes.Role, "CLIENT"),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}
