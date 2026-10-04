using System.Threading;
using System.Threading.Tasks;

namespace Enrichment;

/// <summary>
/// База энричеров: только загрузка данных, без правил. Правила FluentValidation объявляются
/// на стороне валидаторов — <see cref="EnrichingValidator{TRequest}"/> (до загрузки)
/// и <see cref="EnrichingValidator{TRequest, TNeeds}"/> (после, на загруженных данных).
/// </summary>
public abstract class Enricher<TRequest, TData> : IDataEnricher<TRequest, TData>
{
  /// <summary>Загрузить/вычислить payload для генерируемого Enrichment.</summary>
  protected abstract ValueTask<TData> LoadAsync(TRequest request, CancellationToken cancellationToken);

  async ValueTask<object?> IDataEnricher<TRequest>.EnrichAsync(TRequest request, CancellationToken cancellationToken)
    => await LoadAsync(request, cancellationToken).ConfigureAwait(false);
}