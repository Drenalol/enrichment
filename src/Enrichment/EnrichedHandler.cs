using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Enrichment;

/// <summary>
/// Базовый MediatR-хендлер. Прогоняет request-валидаторы до <see cref="HandleAsync"/>,
/// response-валидаторы — после, раскладывает их payload'ы в баги по конкретному типу валидатора.
/// Сгенерированная partial-часть объявляет <c>Enrichment</c>/<c>ResponseEnrichment</c>
/// поверх этих словарей и переопределяет <see cref="RequestValidatorTypes"/>/<see cref="ResponseValidatorTypes"/>.
/// </summary>
public abstract class EnrichedHandler<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
  private readonly IEnrichingValidator<TRequest>[] _requestValidators;
  private readonly IEnrichingValidator<TResponse>[] _responseValidators;

  /// <summary>
  /// Payload'ы request-валидаторов: ключ — конкретный тип валидатора, значение — payload.
  /// Читается сгенерированной partial-частью производного хендлера.
  /// </summary>
  protected readonly Dictionary<Type, object?> RequestBag = new();

  /// <summary>
  /// Payload'ы response-валидаторов; ключ/значение как у <see cref="RequestBag"/>.
  /// </summary>
  protected readonly Dictionary<Type, object?> ResponseBag = new();

  /// <summary>Принимает валидаторы, разрешённые DI: гомогенные <c>IEnumerable&lt;IEnrichingValidator&lt;TRequest&gt;&gt;</c> стороны запроса и ответа.</summary>
  protected EnrichedHandler(
    IEnumerable<IEnrichingValidator<TRequest>> requestValidators,
    IEnumerable<IEnrichingValidator<TResponse>> responseValidators
  )
  {
    _requestValidators = [.. requestValidators];
    _responseValidators = [.. responseValidators];
  }

  /// <summary>
  /// Типы request-валидаторов, известные сгенерированному контексту.
  /// Сгенерированная partial-часть переопределяет этот список своим <c>typeof(...)</c>-массивом;
  /// базовое значение используется до генерации и для guard-проверки.
  /// </summary>
  protected virtual Type[] RequestValidatorTypes => [];

  /// <summary>То же для response-стороны, см. <see cref="RequestValidatorTypes"/>.</summary>
  protected virtual Type[] ResponseValidatorTypes => [];

  /// <inheritdoc />
  public Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken) => HandleCore(request, cancellationToken);

  private async Task<TResponse> HandleCore(TRequest request, CancellationToken cancellationToken)
  {
    foreach (var validator in _requestValidators)
      RequestBag[validator.GetType()] = await validator.EnrichAsync(request, cancellationToken).ConfigureAwait(false);

    Guard(RequestBag, RequestValidatorTypes, "request");

    var response = await HandleAsync(request, cancellationToken).ConfigureAwait(false);

    foreach (var validator in _responseValidators)
      ResponseBag[validator.GetType()] = await validator.EnrichAsync(response, cancellationToken).ConfigureAwait(false);

    Guard(ResponseBag, ResponseValidatorTypes, "response");

    return response;
  }

  /// <summary>
  /// Бизнес-логика хендлера. Вызывается после request-валидаторов (их payload'ы уже в
  /// <see cref="RequestBag"/>) и до response-валидаторов.
  /// </summary>
  protected abstract Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);

  /// <summary>
  /// Fail-fast: DI разрешил набор, отличающийся от известного сгенерированному контексту
  /// (dll-плагин, открытые generics, протухший generated-код).
  /// </summary>
  private static void Guard(Dictionary<Type, object?> bag, Type[] expected, string phase)
  {
    if (expected.Length == 0)
      return;

    if (bag.Keys.All(key => Array.IndexOf(expected, key) >= 0) && expected.All(type => bag.ContainsKey(type)))
      return;

    throw new InvalidOperationException(
      $"Enrichment {phase} validator set does not match the generated context. " +
      $"Resolved by DI: [{string.Join(", ", bag.Keys.Select(key => key.Name))}]; " +
      $"known to generated context: [{string.Join(", ", expected.Select(type => type.Name))}]. " +
      "A validator from an unknown assembly was registered (plugin dll, open generics) or the generated sources are stale and need a rebuild."
    );
  }
}