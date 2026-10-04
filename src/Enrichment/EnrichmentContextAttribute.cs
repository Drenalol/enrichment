using System;

namespace Enrichment;

/// <summary>
/// Необязательное переименование генерируемого контекста стороны хендлера. Без атрибута
/// имя выводится из имени хендлера: <c>CreateOrderHandler</c> → класс <c>CreateOrderEnrichment</c>,
/// интерфейс-витрина <c>ICreateOrderEnrichment</c>. С атрибутом — из его аргументов:
/// класс <c>SalesContext</c>, интерфейс <c>ISalesContext</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EnrichmentContextAttribute : Attribute
{
  /// <summary>Имя класса request-контекста; интерфейс-витрина получает префикс «I». Пусто — имя из хендлера.</summary>
  public string? Request { get; set; }

  /// <summary>Имя класса response-контекста; интерфейс-витрина получает префикс «I». Пусто — имя из хендлера.</summary>
  public string? Response { get; set; }
}