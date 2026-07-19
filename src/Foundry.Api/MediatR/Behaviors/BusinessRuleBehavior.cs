using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace Foundry.Api.MediatR.Behaviors;

/// <summary>
/// Pipeline behavior running Stage 2 (contextual/asynchronous business rules) after structural validations pass.
/// </summary>
public sealed class BusinessRuleBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IBusinessRule<TRequest>> _rules;

    public BusinessRuleBehavior(IEnumerable<IBusinessRule<TRequest>> rules)
    {
        _rules = rules;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (_rules.Any())
        {
            var failures = new List<ValidationFailure>();

            foreach (var rule in _rules)
            {
                var result = await rule.ValidateAsync(request, cancellationToken);
                if (!result.IsPassed)
                {
                    // Map rule failure as a ValidationFailure
                    failures.Add(new ValidationFailure(string.Empty, result.ErrorMessage ?? "Business rule validation failed."));
                }
            }

            if (failures.Any())
            {
                throw new ValidationException(failures);
            }
        }

        return await next();
    }
}
