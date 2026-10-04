using System;
using FluentValidation;

namespace Enrichment;

/// <summary>
/// База чистых валидаторов: только правила FluentValidation, без данных.
/// Хендлер исполняет их до энричеров, поэтому на невалидном запросе не делается ни одного
/// похода во «внешний мир».
/// </summary>
public abstract class EnrichingValidator<TRequest> : AbstractValidator<TRequest>, IEnrichingValidator<TRequest>
{
}

/// <summary>
/// База контекстных валидаторов: правила, которые проверяют уже загруженные данные.
/// Хендлер исполняет их после энричеров и кладёт типизированный Enrichment в
/// <c>ValidationContext.RootContextData</c>; правила достают его через <see cref="GetEnrichment"/>
/// и отказывают честным <c>ValidationFailure</c>, а не исключением загрузчика.
/// </summary>
public abstract class EnrichingValidator<TRequest, TNeeds> : AbstractValidator<TRequest>, IEnrichingValidator<TRequest, TNeeds>
{
  /// <summary>
  /// Контекст обогащения из <c>ValidationContext.RootContextData</c>:
  /// генерируемый Enrichment хендлера, реализующий <typeparamref name="TNeeds"/>.
  /// </summary>
  protected static TNeeds GetEnrichment(IValidationContext context)
    => context.RootContextData.TryGetValue(EnrichmentContext.RootContextKey, out var value) && value is TNeeds needs
       ? needs
       : throw new InvalidOperationException(
           $"The contextual validator needs the '{typeof(TNeeds).Name}' enrichment context, but it was not found in " +
           "ValidationContext.RootContextData. The validator was used outside EnrichedHandler, " +
           "or the generated sources are stale and need a rebuild.");
}