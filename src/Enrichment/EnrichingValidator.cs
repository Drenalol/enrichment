using System.Threading;
using System.Threading.Tasks;
using FluentValidation;

namespace Enrichment;

/// <summary>
/// База для валидаторов с payload: правила FluentValidation объявляешь в конструкторе
/// (<c>RuleFor(...)</c>), данные — в <see cref="EnrichAsync(TRequest, CancellationToken)"/>,
/// который базовый мост вызывает только после успешной валидации правил.
/// </summary>
public abstract class EnrichingValidator<TRequest, TData> : AbstractValidator<TRequest>, IEnrichingValidator<TRequest, TData>
{
  /// <summary>Загрузить/вычислить payload для генерируемого Enrichment. Правила уже пройдены.</summary>
  protected abstract ValueTask<TData> EnrichAsync(TRequest request, CancellationToken cancellationToken);

  async ValueTask<object?> IEnrichingValidator<TRequest>.EnrichAsync(TRequest request, CancellationToken cancellationToken)
  {
    var result = await ((IValidator<TRequest>)this).ValidateAsync(request, cancellationToken).ConfigureAwait(false);

    if (!result.IsValid)
      throw new ValidationException(result.Errors);

    return await EnrichAsync(request, cancellationToken).ConfigureAwait(false);
  }
}

/// <summary>
/// База для валидаторов-чекеров: только правила FluentValidation, payload нет —
/// в генерируемый Enrichment не попадают, но участвуют в runtime-guard.
/// </summary>
public abstract class EnrichingValidator<TRequest> : AbstractValidator<TRequest>, IEnrichingValidator<TRequest>
{
  async ValueTask<object?> IEnrichingValidator<TRequest>.EnrichAsync(TRequest request, CancellationToken cancellationToken)
  {
    var result = await ((IValidator<TRequest>)this).ValidateAsync(request, cancellationToken).ConfigureAwait(false);

    if (!result.IsValid)
      throw new ValidationException(result.Errors);

    return null;
  }
}