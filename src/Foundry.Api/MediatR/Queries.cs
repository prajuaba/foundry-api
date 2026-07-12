using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using MediatR;
using Foundry.Api.MediatR;
using FoundryMongo.Domain.Search;
using FoundryMongo.Domain.Paging;

namespace Foundry.Api.MediatR;

public record GetByIdQuery<TEntity>(ObjectId Id) : IRequest<TEntity?>
    where TEntity : class, IEntity<ObjectId>;

public record FindManyQuery<TEntity>(
    Expression<Func<TEntity, bool>>? Filter = null,
    string? SortBy = null,
    SortOrder SortOrder = SortOrder.Descending,
    int Limit = 100,
    SearchCriterion[]? Criteria = null) : IRequest<IReadOnlyList<TEntity>>
    where TEntity : class, IEntity<ObjectId>;

public record SearchPagedQuery<TEntity>(
    SearchCriterion[] Criteria,
    PagedRequest PageRequest) : IRequest<PagedResult<TEntity>>
    where TEntity : class, IEntity<ObjectId>;
