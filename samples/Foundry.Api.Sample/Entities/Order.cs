using System;
using MongoDB.Bson;
using Foundry.Core.Entities;

namespace Paperclip.OrderingSystem.Domain;

public record Order : BaseEntity<ObjectId>, IVersionable, ISoftDelete
{
    [Indexed]
    public string CustomerId { get; init; } = string.Empty;

    [Indexed(Unique = true)]
    [TextIndexed]
    public required string OrderNumber { get; init; } = string.Empty;

    public decimal TotalAmount { get; init; } = 0;

    public OrderStatus Status { get; init; } = default(OrderStatus);

    [SensitiveData(Protection = ProtectionType.Encrypt)]
    public string SecretToken { get; init; } = string.Empty;

    [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email)]
    public string UserEmail { get; init; } = string.Empty;

    [Indexed]
    public bool IsDeleted { get; init; } = false;

    public DateTime? DeletedAt { get; init; }
}
