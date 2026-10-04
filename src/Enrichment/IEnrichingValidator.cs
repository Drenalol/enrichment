using FluentValidation;

namespace Enrichment;

/// <summary>
/// Базовый контракт валидатора: именно его инжектирует DI в хендлер и именно его сканирует Scrutor.
/// Наследует FluentValidation-контракт, поэтому валидатор — полноценный FV-валидатор.
/// Сам ничего не загружает: данные кладёт <see cref="IDataEnricher{TRequest}"/>,
/// а хендлер раскладывает их в генерируемый Enrichment.
/// </summary>
public interface IEnrichingValidator<TRequest> : IValidator<TRequest>
{
}

/// <summary>
/// Маркер фазы: контекстные валидаторы (<see cref="IEnrichingValidator{TRequest, TNeeds}"/>)
/// исполняются после энричеров и читают данные из <c>ValidationContext.RootContextData</c>.
/// Чистые валидаторы (только <see cref="IEnrichingValidator{TRequest}"/>) исполняются до любого I/O.
/// </summary>
public interface IContextualEnrichingValidator
{
}

/// <summary>
/// Контекстный валидатор: <typeparamref name="TNeeds"/> — сгенерированный интерфейс-витрина
/// контекста (I{Context}Enrichment), который генератор эмитит из payload'ов энричеров этой
/// стороны и который реализует генерируемый Enrichment. Правила читают данные через
/// <c>GetEnrichment(context)</c>; рукописные needs-интерфейсы запрещены (ENR006),
/// а ссылка на несуществующий член контекста — ошибка компиляции на месте правила.
/// </summary>
public interface IEnrichingValidator<TRequest, TNeeds> : IEnrichingValidator<TRequest>, IContextualEnrichingValidator
{
}