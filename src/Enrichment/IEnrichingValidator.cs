using System.Threading;
using System.Threading.Tasks;
using FluentValidation;

namespace Enrichment;

/// <summary>
/// Базовый контракт: именно его инжектирует DI в хендлер и именно его сканирует Scrutor.
/// Наследует FluentValidation-контракт, поэтому валидатор — полноценный FV-валидатор:
/// правила объявляются в конструкторе, правила гоняются базовым мостом до загрузки данных.
/// <see cref="EnrichAsync"/> возвращает payload как object; типизацию возвращает генерируемый код.
/// </summary>
public interface IEnrichingValidator<TRequest> : IValidator<TRequest>
{
  /// <summary>
  /// Полный проход: правила FluentValidation, затем (при успехе) загрузка payload'а.
  /// Возвращает payload как <see cref="object"/>; типизированный доступ даёт генерируемый Enrichment.
  /// </summary>
  ValueTask<object?> EnrichAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Маркер payload-типа: source generator читает <typeparamref name="TData"/> из метаданных
/// и разворачивает его в плоские свойства генерируемого Enrichment
/// (именованный кортеж — по элементам, класс/record — по публичным свойствам).
/// В рантайме конвейер вызывает только <see cref="IEnrichingValidator{TRequest}.EnrichAsync"/>.
/// </summary>
public interface IEnrichingValidator<TRequest, TData> : IEnrichingValidator<TRequest>
{
}