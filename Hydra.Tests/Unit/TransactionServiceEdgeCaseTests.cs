using System.Text.Json;
using Moq;
using Xunit;
using Hydra.Application.Services;
using Hydra.Domain.Entities;
using Hydra.Domain.Enums;
using Hydra.Infrastructure.DATA;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Threading.Tasks;

namespace Hydra.Tests.Unit;

public class TransactionServiceWithRealDbTests
{
    private static BankOsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BankOsDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        var context = new BankOsDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    private static async Task<(Guid tenantId, Guid ownerId, Guid otherUserId, Account source, Account destination, Tenant tenant)> SeedBasicTransferAsync()
    {
        var db = CreateContext();
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Test Tenant",
            Slug = "test-tenant",
            MainCurrency = "COP",
            MaxTransactionAmount = 10_000_000m,
            FeeType = FeeTypeEnum.FIXED,
            FeeValue = 1000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var owner = new User
        {
            Id = ownerId,
            TenantId = tenantId,
            FullName = "Owner",
            DocumentNumber = "OWNER-DOC",
            Email = "owner@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var otherUser = new User
        {
            Id = otherUserId,
            TenantId = tenantId,
            FullName = "Other User",
            DocumentNumber = "OTHER-DOC",
            Email = "other@test.com",
            Role = UserRole.CLIENT,
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var source = new Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OwnerId = ownerId,
            AccountNumber = "3000000001",
            Balance = 500000m,
            Status = AccountStatus.ACTIVE,
            Currency = "COP",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var destination = new Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OwnerId = otherUserId,
            AccountNumber = "3000000002",
            Balance = 100000m,
            Status = AccountStatus.ACTIVE,
            Currency = "COP",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Tenants.Add(tenant);
        db.BankUsers.AddRange(owner, otherUser);
        db.Accounts.AddRange(source, destination);
        await db.SaveChangesAsync();

        return (tenantId, otherUserId, ownerId, source, destination, tenant);
    }

    [Theory]
    [InlineData(50000, 1000, 101000, 510000, 150000)]
    [InlineData(100, 1000, 1100, 499900, 100100)]
    [InlineData(1, 1000, 1001, 499999, 100001)]
    public async Task TransferAsync_FixedFee_CalculatesBalancesCorrectly(
        decimal transferAmount, decimal fee, decimal totalDebit,
        decimal expectedSourceBalance, decimal expectedDestBalance)
    {
        var db = CreateContext();
        var (tenantId, _, _, source, destination, tenant) = await SeedBasicTransferAsync();

        var mockIdempotency = new Mock<Hydra.Application.Interfaces.IIdempotencyService>();
        mockIdempotency.Setup(x => x.StartProcessingAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        mockIdempotency.Setup(x => x.CompleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>()))
            .Returns(Task.CompletedTask);

        var mockWebhook = new Mock<Hydra.Application.Interfaces.IWebhookNotifier>();
        mockWebhook.Setup(x => x.NotifyAsync(It.IsAny<string>(), It.IsAny<object>()))
            .Returns(Task.CompletedTask);

        var service = new TransactionService(db, mockIdempotency.Object, mockWebhook.Object);
        var userId = source.OwnerId;

        var request = new TransferRequestDto
        {
            DestinationDocumentNumber = destination.User.DocumentNumber,
            Amount = transferAmount
        };

        var result = await service.TransferAsync(tenantId, userId, request, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        Assert.True(result.Status == "SUCCESS");
        Assert.Equal(expectedSourceBalance, result.SourceBalance);
        Assert.Equal(expectedDestBalance, result.DestinationBalance);
    }

    [Theory]
    [InlineData(5_000_001)]
    [InlineData(10_000_000)]
    [InlineData(100_000_000)]
    public async Task TransferAsync_ExceedingMaxTransactionAmount_ThrowsException(decimal amount)
    {
        var db = CreateContext();
        var (tenantId, _, _, source, destination, _) = await SeedBasicTransferAsync();

        var mockIdempotency = new Mock<Hydra.Application.Interfaces.IIdempotencyService>();
        var mockWebhook = new Mock<Hydra.Application.Interfaces.IWebhookNotifier>();

        var service = new TransactionService(db, mockIdempotency.Object, mockWebhook.Object);

        var request = new TransferRequestDto
        {
            DestinationDocumentNumber = destination.User.DocumentNumber,
            Amount = amount
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TransferAsync(tenantId, source.OwnerId, request, Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task TransferToSelf_ThrowsException()
    {
        var db = CreateContext();
        var (tenantId, _, _, source, _, _) = await SeedBasicTransferAsync();

        var mockIdempotency = new Mock<Hydra.Application.Interfaces.IIdempotencyService>();
        var mockWebhook = new Mock<Hydra.Application.Interfaces.IWebhookNotifier>();

        var service = new TransactionService(db, mockIdempotency.Object, mockWebhook.Object);

        var request = new TransferRequestDto
        {
            DestinationDocumentNumber = source.User.DocumentNumber,
            Amount = 1000
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TransferAsync(tenantId, source.OwnerId, request, Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task TransferInsufficientBalance_ThrowsException()
    {
        var db = CreateContext();
        var (tenantId, _, _, source, destination, _) = await SeedBasicTransferAsync();

        var mockIdempotency = new Mock<Hydra.Application.Interfaces.IIdempotencyService>();
        var mockWebhook = new Mock<Hydra.Application.Interfaces.IWebhookNotifier>();

        var service = new TransactionService(db, mockIdempotency.Object, mockWebhook.Object);

        var request = new TransferRequestDto
        {
            DestinationDocumentNumber = destination.User.DocumentNumber,
            Amount = 600000
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TransferAsync(tenantId, source.OwnerId, request, Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
    }
}
