using System;
using System.Collections.Generic;

namespace Hydra.Infrastructure.Entidades;

public partial class ExchangeRate
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string FromCurrency { get; set; } = null!;

    public string ToCurrency { get; set; } = null!;

    public decimal Rate { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Tenant Tenant { get; set; } = null!;
}
