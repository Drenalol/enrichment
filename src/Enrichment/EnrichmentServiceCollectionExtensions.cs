using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Scrutor;

namespace Enrichment;

/// <summary>
/// Регистрация валидаторов через Scrutor-сканирование сборок.
/// </summary>
public static class EnrichmentServiceCollectionExtensions
{
  /// <summary>
  /// Находит во всех закрытых реализациях <see cref="IEnrichingValidator{TRequest}"/>
  /// в переданных сборках и регистрирует каждую под её закрытыми <see cref="IEnrichingValidator{TRequest}"/>
  /// (и FluentValidation-интерфейсами) с преходящим сроком службы.
  /// Это тот же набор, что DI потом инжектит хендлеру как <c>IEnumerable&lt;IEnrichingValidator&lt;TRequest&gt;&gt;</c>.
  /// Генератору безразлично, откуда DI берёт инъект: форму Enrichment он собирает по типам валидаторов, видимым на компиляции.
  /// </summary>
  /// <param name="services">Целевая коллекция сервисов.</param>
  /// <param name="assemblies">Сборки для сканирования. Если не переданы — сканируется вызывающая сборка.</param>
  public static IServiceCollection AddEnrichmentValidators(this IServiceCollection services, params Assembly[] assemblies)
  {
    ArgumentNullException.ThrowIfNull(services);

    var scanAssemblies = assemblies is null || assemblies.Length == 0
        ? new[] { Assembly.GetCallingAssembly() }
        : assemblies;

    services.Scan(scan => scan
        .FromAssemblies(scanAssemblies)
        .AddClasses(classes => classes.AssignableTo(typeof(IEnrichingValidator<>)))
        .AsImplementedInterfaces()
        .WithTransientLifetime());

    return services;
  }
}