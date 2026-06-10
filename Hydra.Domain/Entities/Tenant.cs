using System;
using System.Collections.Generic;
using Hydra.Domain.Enums;

namespace Hydra.Domain.Entities;

public partial class Tenant
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public string MainCurrency { get; set; } = null!;

    public decimal MaxTransactionAmount { get; set; }

    public FeeTypeEnum FeeType { get; set; }

    public decimal FeeValue { get; set; }

    public string? WebhookUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
