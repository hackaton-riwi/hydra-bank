using Hydra.Domain.Enums;

namespace Hydra.Application.DTOs;

public class TransactionHistoryQueryDto
{
    public int Limit { get; set; } = 20;

    public int Offset { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public TransactionType? Type { get; set; }
}

public class RechargeAccountDto
{
    public decimal Amount { get; set; }
}
