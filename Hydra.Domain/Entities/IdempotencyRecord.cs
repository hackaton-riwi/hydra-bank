using System;
using System.Collections.Generic;
using Hydra.Domain.Enums;

namespace Hydra.Domain.Entities;

public partial class IdempotencyRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid IdempotencyKey { get; set; }

    public string RequestHash { get; set; } = null!;

    public string? ResponseBody { get; set; }

    public int? StatusCode { get; set; }

    public IdempotencyState State { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public virtual User User { get; set; } = null!;
}
