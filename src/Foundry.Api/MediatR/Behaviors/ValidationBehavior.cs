using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using FluentValidation;
using MediatR;

namespace Foundry.Api.MediatR.Behaviors;

public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IServiceProvider _serviceProvider;

    public ValidationBehavior(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestType = typeof(TRequest);
        var entityProp = requestType.GetProperty("Entity");
        
        if (entityProp != null)
        {
            var entityVal = entityProp.GetValue(request);
            if (entityVal != null)
            {
                var entityType = entityVal.GetType();
                var validatorType = typeof(IValidator<>).MakeGenericType(entityType);
                var validator = _serviceProvider.GetService(validatorType) as IValidator;
                
                if (validator != null)
                {
                    var context = new ValidationContext<object>(entityVal);
                    var result = await validator.ValidateAsync(context, cancellationToken);
                    
                    if (!result.IsValid)
                    {
                        // Increment validation failure counter
                        Diagnostics.Diagnostics.ValidationFailures.Add(1, new KeyValuePair<string, object?>("entity_type", entityType.Name));
                        
                        // Tag current OpenTelemetry activity
                        var activity = Activity.Current;
                        if (activity != null)
                        {
                            activity.SetTag("foundry.validation.status", "Failed");
                            activity.SetTag("foundry.validation.errors_count", result.Errors.Count);
                        }

                        throw new ValidationException(result.Errors);
                    }
                }
            }
        }

        return await next();
    }
}
