using System;
using System.Collections.Generic;
using Hydra.Domain.Enums;

namespace Hydra.Domain.Entities;

public partial class Account
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid OwnerId { get; set; }

    public string AccountNumber { get; set; } = null!;

    public decimal Balance { get; set; }

    public string Currency { get; set; } = null!;

    public AccountStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeactivatedAt { get; set; }

    public virtual ICollection<Transaction> TransactionAccountNavigations { get; set; } = new List<Transaction>();

    public virtual ICollection<Transaction> TransactionAccounts { get; set; } = new List<Transaction>();

    public virtual User User { get; set; } = null!;
}
