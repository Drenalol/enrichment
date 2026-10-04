using System.Threading;
using System.Threading.Tasks;

namespace Enrichment;

/// <summary>
/// Контракт слоя данных: энричер только грузит/вычисляет payload из «внешнего мира»
/// (репозитории, HTTP-клиенты) — правил валидации у него нет, они живут на стороне
/// <see cref="IEnrichingValidator{TRequest}"/>. DI инжектит хендлеру
/// <c>IEnumerable&lt;IDataEnricher&lt;TRequest&gt;&gt;</c>.
/// </summary>
public interface IDataEnricher<TRequest>
{
  /// <summary>Загрузить payload как <see cref="object"/>; типизированный доступ даёт генерируемый Enrichment.</summary>
  ValueTask<object?> EnrichAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Маркер payload-типа: source generator читает <typeparamref name="TData"/> из метаданных
/// и разворачивает его в плоские свойства генерируемого Enrichment
/// (именованный кортеж — по элементам, класс/record — по публичным свойствам).
/// </summary>
public interface IDataEnricher<TRequest, TData> : IDataEnricher<TRequest>
{
}