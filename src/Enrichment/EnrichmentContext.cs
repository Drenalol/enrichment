namespace Enrichment;

/// <summary>
/// Ключ, под которым хендлер кладёт типизированный Enrichment в <c>ValidationContext.RootContextData</c>
/// перед прогоном контекстных валидаторов. Пишется базовым <c>EnrichedHandler</c>,
/// читается <c>EnrichingValidator&lt;TRequest, TNeeds&gt;.GetEnrichment</c>.
/// </summary>
public static class EnrichmentContext
{
  /// <summary>Имя ключа в ValidationContext.RootContextData.</summary>
  public const string RootContextKey = "Enrichment.Context";
}