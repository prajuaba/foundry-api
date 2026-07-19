using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Api.MediatR;

/// <summary>
/// Result definition of a business rule execution.
/// </summary>
public record RuleResult(bool IsPassed, string? ErrorMessage = null)
{
    /// <summary>Creates a successful rule result.</summary>
    public static RuleResult Success() => new(true);

    /// <summary>Creates a failed rule result with the specified error message.</summary>
    public static RuleResult Failure(string message) => new(false, message);
}

/// <summary>
/// Contract for custom asynchronous business rule validations executed within command pipelines.
/// </summary>
public interface IBusinessRule<in TRequest>
{
    /// <summary>
    /// Evaluates the business rule against the incoming command request context.
    /// </summary>
    Task<RuleResult> ValidateAsync(TRequest request, CancellationToken ct);
}
