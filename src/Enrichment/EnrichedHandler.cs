using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using MediatR;

namespace Enrichment;

/// <summary>
/// Базовый MediatR-хендлер с четырёхфазным конвейером:
/// 1) чистые request-валидаторы — до любого I/O;
/// 2) request-энричеры — грузят payload'ы в <see cref="RequestBag"/>;
/// 3) контекстные request-валидаторы — проверяют на сгенерированном Enrichment;
/// 4) <see cref="HandleAsync"/>; затем та же тройка фаз на response-стороне.
/// Сгенерированная partial-часть объявляет <c>Enrichment</c>/<c>ResponseEnrichment</c>
/// поверх багов и переопределяет <see cref="RequestEnricherTypes"/>/<see cref="RequestValidatorTypes"/>
/// и <see cref="CreateRequestContext"/>.
/// </summary>
public abstract class EnrichedHandler<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
  private readonly IDataEnricher<TRequest>[] _requestEnrichers;
  private readonly IEnrichingValidator<TRequest>[] _requestValidators;
  private readonly IDataEnricher<TResponse>[] _responseEnrichers;
  private readonly IEnrichingValidator<TResponse>[] _responseValidators;

  /// <summary>
  /// Payload'ы request-энричеров: ключ — конкретный тип энричера, значение — payload.
  /// Читается сгенерированной partial-частью производного хендлера.
  /// </summary>
  protected readonly Dictionary<Type, object?> RequestBag = new();

  /// <summary>
  /// Payload'ы response-энричеров; ключ/значение как у <see cref="RequestBag"/>.
  /// </summary>
  protected readonly Dictionary<Type, object?> ResponseBag = new();

  /// <summary>Принимает энричеры и валидаторы обеих сторон, разрешённые DI.</summary>
  protected EnrichedHandler(
    IEnumerable<IDataEnricher<TRequest>> requestEnrichers,
    IEnumerable<IEnrichingValidator<TRequest>> requestValidators,
    IEnumerable<IDataEnricher<TResponse>> responseEnrichers,
    IEnumerable<IEnrichingValidator<TResponse>> responseValidators
  )
  {
    _requestEnrichers = [.. requestEnrichers];
    _requestValidators = [.. requestValidators];
    _responseEnrichers = [.. responseEnrichers];
    _responseValidators = [.. responseValidators];
  }

  /// <summary>
  /// Типы request-энричеров, известные сгенерированному контексту.
  /// Сгенерированная partial-часть переопределяет этот список своим <c>typeof(...)</c>-массивом;
  /// базовое значение используется до генерации и для guard-проверки.
  /// </summary>
  protected virtual Type[] RequestEnricherTypes => [];

  /// <summary>То же для request-валидаторов (чистых и контекстных вместе).</summary>
  protected virtual Type[] RequestValidatorTypes => [];

  /// <summary>То же для response-стороны, см. <see cref="RequestEnricherTypes"/>.</summary>
  protected virtual Type[] ResponseEnricherTypes => [];

  /// <summary>То же для response-валидаторов.</summary>
  protected virtual Type[] ResponseValidatorTypes => [];

  /// <summary>
  /// Типизированный контекст для контекстных request-валидаторов.
  /// Сгенерированная partial-часть переопределяет: <c>() =&gt; Enrichment</c>.
  /// </summary>
  protected virtual object? CreateRequestContext() => null;

  /// <summary>То же для response-стороны, см. <see cref="CreateRequestContext"/>.</summary>
  protected virtual object? CreateResponseContext() => null;

  /// <inheritdoc />
  public Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken) => HandleCore(request, cancellationToken);

  private async Task<TResponse> HandleCore(TRequest request, CancellationToken cancellationToken)
  {
    GuardSet([.. _requestValidators.Select(static validator => validator.GetType())], RequestValidatorTypes, "request");

    foreach (var validator in _requestValidators)
      if (validator is not IContextualEnrichingValidator)
        await ValidateAsync(validator, request, null, cancellationToken).ConfigureAwait(false);

    foreach (var enricher in _requestEnrichers)
      RequestBag[enricher.GetType()] = await enricher.EnrichAsync(request, cancellationToken).ConfigureAwait(false);

    GuardBag(RequestBag, RequestEnricherTypes, "request");

    var requestContext = CreateRequestContext();

    foreach (var validator in _requestValidators)
      if (validator is IContextualEnrichingValidator)
        await ValidateAsync(validator, request, requestContext, cancellationToken).ConfigureAwait(false);

    var response = await HandleAsync(request, cancellationToken).ConfigureAwait(false);

    GuardSet([.. _responseValidators.Select(static validator => validator.GetType())], ResponseValidatorTypes, "response");

    foreach (var validator in _responseValidators)
      if (validator is not IContextualEnrichingValidator)
        await ValidateAsync(validator, response, null, cancellationToken).ConfigureAwait(false);

    foreach (var enricher in _responseEnrichers)
      ResponseBag[enricher.GetType()] = await enricher.EnrichAsync(response, cancellationToken).ConfigureAwait(false);

    GuardBag(ResponseBag, ResponseEnricherTypes, "response");

    var responseContext = CreateResponseContext();

    foreach (var validator in _responseValidators)
      if (validator is IContextualEnrichingValidator)
        await ValidateAsync(validator, response, responseContext, cancellationToken).ConfigureAwait(false);

    return response;
  }

  private static async Task ValidateAsync<TMessage>(
    IEnrichingValidator<TMessage> validator,
    TMessage message,
    object? enrichmentContext,
    CancellationToken cancellationToken)
  {
    var context = new ValidationContext<TMessage>(message);

    if (enrichmentContext is not null)
      context.RootContextData[EnrichmentContext.RootContextKey] = enrichmentContext;

    var result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);

    if (!result.IsValid)
      throw new ValidationException(result.Errors);
  }

  /// <summary>
  /// Бизнес-логика хендлера. Вызывается после request-фаз (payload'ы уже в
  /// <see cref="RequestBag"/>, контекст доступен через <c>Enrichment</c>).
  /// </summary>
  protected abstract Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);

  /// <summary>
  /// Fail-fast: DI разрешил payload'-набор, отличающийся от известного сгенерированному контексту
  /// (dll-плагин, открытые generics, протухший generated-код).
  /// </summary>
  private static void GuardBag(Dictionary<Type, object?> bag, Type[] expected, string phase)
  {
    if (expected.Length == 0)
      return;

    if (bag.Keys.All(key => Array.IndexOf(expected, key) >= 0) && expected.All(type => bag.ContainsKey(type)))
      return;

    throw new InvalidOperationException(
      $"Enrichment {phase} enricher set does not match the generated context. " +
      $"Resolved by DI: [{string.Join(", ", bag.Keys.Select(key => key.Name))}]; " +
      $"known to generated context: [{string.Join(", ", expected.Select(type => type.Name))}]. " +
      "An enricher from an unknown assembly was registered (plugin dll, open generics) or the generated sources are stale and need a rebuild."
    );
  }

  /// <summary>
  /// Fail-fast: DI разрешил набор валидаторов, отличающийся от известного сгенерированной partial-частью.
  /// </summary>
  private static void GuardSet(Type[] actual, Type[] expected, string phase)
  {
    if (expected.Length == 0)
      return;

    if (expected.Length == actual.Length && expected.All(type => actual.Contains(type)))
      return;

    throw new InvalidOperationException(
      $"Enrichment {phase} validator set does not match the generated context. " +
      $"Resolved by DI: [{string.Join(", ", actual.Select(type => type.Name))}]; " +
      $"known to generated context: [{string.Join(", ", expected.Select(type => type.Name))}]. " +
      "A validator from an unknown assembly was registered (plugin dll, open generics) or the generated sources are stale and need a rebuild."
    );
  }
}