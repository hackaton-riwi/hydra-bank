using Hydra.Domain.Enums;

namespace Hydra.Application.DTOs;

public class MoneyOperationDto
{
    public decimal Amount { get; set; }
}

public class TransferDto
{
    public Guid SourceAccountId { get; set; }

    public Guid DestinationAccountId { get; set; }

    public decimal Amount { get; set; }
}

public class TransactionHistoryQueryDto
{
    public int Limit { get; set; } = 20;

    public int Offset { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public TransactionType? Type { get; set; }
}

public sealed record ServiceResponse(int StatusCode, object Body);
