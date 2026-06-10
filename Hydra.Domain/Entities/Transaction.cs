using System;
using System.Collections.Generic;
using Hydra.Domain.Enums;

namespace Hydra.Domain.Entities;

public partial class Transaction
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public TransactionType Type { get; set; }

    public Guid? SourceAccountId { get; set; }

    public Guid? DestinationAccountId { get; set; }

    public decimal OriginalAmount { get; set; }

    public decimal? FeeAmount { get; set; }

    public TransactionStatus Status { get; set; }

    public Guid CorrelationId { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Account? Account { get; set; }

    public virtual Account? AccountNavigation { get; set; }

    public virtual User User { get; set; } = null!;
}
