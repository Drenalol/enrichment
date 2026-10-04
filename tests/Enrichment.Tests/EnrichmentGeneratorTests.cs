using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Enrichment.Generator;
using Xunit;

namespace Enrichment.Tests;

public class EnrichmentGeneratorTests
{
  private const string Model = """
    using Enrichment;
    using FluentValidation;
    using MediatR;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    public class RequestOne : IRequest<ResponseOne>
    {
        public string OrderId { get; set; }

        public string Title { get; set; }
    }

    public class ResponseOne
    {
        public string Echo { get; set; }
    }

    public interface IOrderType
    {
    }

    public class Order : IOrderType
    {
    }
    """;

  private const string HandlerCtor = """
      public RequestOneHandler(IEnumerable<IDataEnricher<RequestOne>> requestEnrichers, IEnumerable<IEnrichingValidator<RequestOne>> requestValidators, IEnumerable<IDataEnricher<ResponseOne>> responseEnrichers, IEnumerable<IEnrichingValidator<ResponseOne>> responseValidators) : base(requestEnrichers, requestValidators, responseEnrichers, responseValidators)
      {
      }
    """;

  private static CSharpMetadata ReferenceOf(string source, string assemblyName)
  {
    var compilation = CSharpCompilation.Create(
        assemblyName,
        [CSharpSyntaxTree.ParseText(source)],
        BaseReferences(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    using var stream = new MemoryStream();
    var result = compilation.Emit(stream);
    Assert.True(result.Success, string.Join("; ", result.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)));

    return new CSharpMetadata(MetadataReference.CreateFromImage(stream.ToArray()));
  }

  private sealed record CSharpMetadata(MetadataReference Reference);

  private static MetadataReference[] BaseReferences()
  {
    var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

    return
    [
      .. trusted
          .Split(Path.PathSeparator)
          .Where(static path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
          .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)),
      MetadataReference.CreateFromFile(typeof(IEnrichingValidator<>).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(IDataEnricher<>).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(FluentValidation.AbstractValidator<>).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(MediatR.IRequestHandler<,>).Assembly.Location),
      MetadataReference.CreateFromFile(typeof(MediatR.IRequest).Assembly.Location),
    ];
  }

  private static (ImmutableArray<Diagnostic> Generator, Diagnostic[] CompilationErrors, IReadOnlyDictionary<string, string> Files) RunProbingWarnings(string source, out string[] warnings, params MetadataReference[] extraReferences)
  {
    var r = RunCore(source, extraReferences);
    warnings = [.. r.Produced.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Warning && d.Id == "CS8603").Select(static d => d.ToString())];

    return (r.Run.Diagnostics, r.Errors, r.Files);
  }

  private static (Compilation Produced, Diagnostic[] Errors, IReadOnlyDictionary<string, string> Files, GeneratorDriverRunResult Run) RunCore(string source, params MetadataReference[] extraReferences)
  {
    MetadataReference[] references = [.. BaseReferences().Concat(extraReferences)];

    var compilation = CSharpCompilation.Create(
        "Consumer",
        [CSharpSyntaxTree.ParseText(source)],
        references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    var driver = CSharpGeneratorDriver.Create(new EnrichmentGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out var produced, out _);

    var run = driver.GetRunResult();

    return (
        produced,
        [.. produced.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)],
        run.GeneratedTrees.ToDictionary(static tree => Path.GetFileName(tree.FilePath), static tree => tree.ToString()),
        run);
  }

  private static (ImmutableArray<Diagnostic> Generator, Diagnostic[] CompilationErrors, IReadOnlyDictionary<string, string> Files) Run(string source, params MetadataReference[] extraReferences)
  {
    var r = RunCore(source, extraReferences);

    return (r.Run.Diagnostics, r.Errors, r.Files);
  }

  [Fact]
  public void NamedTuplePayload_FlattensToTypedMembers_AndKeepsPureValidatorInGuard()
  {
    var source = Model + """

      public class OrderEnricher : Enricher<RequestOne, (Order Order, IDictionary<string, IOrderType> OrdersToPay)>
      {
          protected override ValueTask<(Order Order, IDictionary<string, IOrderType> OrdersToPay)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class NameValidator : EnrichingValidator<RequestOne>
      {
          public NameValidator()
          {
              RuleFor(x => x.Title).NotEmpty();
          }
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var order = Enrichment.Order;
              var ordersToPay = Enrichment.OrdersToPay;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (generator, errors, files) = Run(source);

    Assert.Empty(errors);
    Assert.Empty(generator.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    var context = files["RequestOneHandler.RequestOneEnrichment.g.cs"];
    Assert.Contains("public global::Order Order =>", context);
    Assert.Contains("public global::System.Collections.Generic.IDictionary<string, global::IOrderType> OrdersToPay =>", context);

    // имя члена проброшено в Get: и payload-less, и null-payload провалы называют член по имени
    Assert.Contains("Get(typeof(global::OrderEnricher), \"Order\")", context);
    Assert.Contains("private object? Get(global::System.Type enricher, string member)", context);
    Assert.DoesNotContain("\"?\"", context);

    var handler = files["RequestOneHandler.Handler.g.cs"];
    Assert.Contains("typeof(global::OrderEnricher)", handler);
    Assert.Contains("typeof(global::NameValidator)", handler);

    // чистый валидатор — только в guard-массиве валидаторов, не энричеров
    Assert.Contains("RequestEnricherTypes { get; } = new global::System.Type[] { typeof(global::OrderEnricher) };", handler);
  }

  [Fact]
  public void UnnamedTuplePayload_YieldsItemNames()
  {
    var source = Model + """

      public class OrderEnricher : Enricher<RequestOne, (Order, IDictionary<string, IOrderType>)>
      {
          protected override ValueTask<(Order, IDictionary<string, IOrderType>)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var first = Enrichment.Item1;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (_, errors, files) = Run(source);

    Assert.Empty(errors);
    Assert.Contains("public global::Order Item1 =>", files["RequestOneHandler.RequestOneEnrichment.g.cs"]);
  }

  [Fact]
  public void PureValidator_WithoutEnrichers_TrackedInGuard_AndNoContextClass()
  {
    var source = Model + """

      public class NameValidator : EnrichingValidator<RequestOne>
      {
          public NameValidator()
          {
              RuleFor(x => x.Title).NotEmpty();
          }
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
              => Task.FromResult(new ResponseOne());
      }
      """;

    var (_, errors, files) = Run(source);

    Assert.Empty(errors);

    Assert.False(files.ContainsKey("RequestOneHandler.RequestOneEnrichment.g.cs"));

    var handler = files["RequestOneHandler.Handler.g.cs"];
    Assert.Contains("typeof(global::NameValidator)", handler);
    Assert.DoesNotContain("CreateRequestContext", handler);
  }

  [Fact]
  public void DuplicateMemberNames_ReportError_AndDropMembers()
  {
    var source = Model + """

      public class FirstEnricher : Enricher<RequestOne, (Order Foo, int IgnoredOne)>
      {
          protected override ValueTask<(Order, int)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class SecondEnricher : Enricher<RequestOne, (string Foo, int IgnoredTwo)>
      {
          protected override ValueTask<(string, int)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
              => Task.FromResult(new ResponseOne());
      }
      """;

    var (generator, errors, files) = Run(source);

    Assert.Empty(errors);
    Assert.Contains(generator, static diagnostic => diagnostic.Id == "ENR002");

    var context = files["RequestOneHandler.RequestOneEnrichment.g.cs"];
    Assert.DoesNotContain("public global::Order Foo =>", context);
    Assert.DoesNotContain("public string Foo =>", context);
    Assert.Contains("IgnoredOne", context);
    Assert.Contains("IgnoredTwo", context);
  }

  [Fact]
  public void AtomicPayload_ReportsError()
  {
    var source = Model + """

      public class StringPayloadEnricher : Enricher<RequestOne, string>
      {
          protected override ValueTask<string> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
              => Task.FromResult(new ResponseOne());
      }
      """;

    var (generator, errors, _) = Run(source);

    Assert.Empty(errors);
    Assert.Contains(generator, static diagnostic => diagnostic.Id == "ENR001");
  }

  [Fact]
  public void NonPartialHandler_CompilerReportsPartialRequirement()
  {
    var source = Model + """

      public class OrderEnricher : Enricher<RequestOne, (Order Order, int Count)>
      {
          protected override ValueTask<(Order, int)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
              => Task.FromResult(new ResponseOne());
      }
      """;

    var (_, errors, _) = Run(source);

    Assert.Contains(errors, static diagnostic => diagnostic.Id == "CS0260");
  }

  [Fact]
  public void EnricherFromReferencedAssembly_IsDiscoveredAndTyped()
  {
    var enricherSource = """
      using Enrichment;
      using System.Collections.Generic;
      using System.Threading;
      using System.Threading.Tasks;

      public class RequestOne
      {
          public string OrderId { get; set; }
      }

      public interface IOrderType
      {
      }

      public class Order
      {
      }

      public class ReferenceOrderEnricher : Enricher<RequestOne, (Order Order, IDictionary<string, IOrderType> OrdersToPay)>
      {
          protected override ValueTask<(Order Order, IDictionary<string, IOrderType> OrdersToPay)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }
      """;

    var reference = ReferenceOf(enricherSource, "Enrichers");

    var source = Model + """

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var order = Enrichment.Order;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (_, errors, files) = Run(source, reference.Reference);

    Assert.Empty(errors);
    Assert.Contains("typeof(global::ReferenceOrderEnricher)", files["RequestOneHandler.Handler.g.cs"]);
    Assert.Contains("public global::Order Order =>", files["RequestOneHandler.RequestOneEnrichment.g.cs"]);
  }

  [Fact]
  public void NullablePayloadElements_PreserveNullability_AndNullPayloadThrows()
  {
    var source = "#nullable enable\n" + Model + """

      public class NullablePayloadEnricher : Enricher<RequestOne, (Order? Order, decimal? Optional, string Required)>
      {
          protected override ValueTask<(Order?, decimal?, string)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var order = Enrichment.Order;
              var optional = Enrichment.Optional;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (_, errors, files) = RunProbingWarnings(source, out var cs8603);

    Assert.Empty(errors);
    Assert.Empty(cs8603);

    var context = files["RequestOneHandler.RequestOneEnrichment.g.cs"];

    // ссылочная nullable-аннотация сохранена как Member-тип
    Assert.Contains("public global::Order? Order =>", context);

    // Nullable<decimal> — сам тип, двойного «?» быть не должно
    Assert.Contains("public decimal? Optional =>", context);
    Assert.DoesNotContain("decimal?? ", context);

    // null payload (Get вернёт null, type-pattern провалится) — fail-fast с честным текстом
    Assert.Contains("returned a null payload", context);
  }

  [Fact]
  public void ContextualValidator_AutoUnionInterface_EmittedFromPayloads_AndValidatorTrackedInGuard()
  {
    var source = "#nullable enable\n" + Model + """

      public class OrderEnricher : Enricher<RequestOne, (Order? Order, IDictionary<string, IOrderType> OrdersToPay)>
      {
          protected override ValueTask<(Order?, IDictionary<string, IOrderType>)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class OrderExistsValidator : EnrichingValidator<RequestOne, IRequestOneEnrichment>
      {
          public OrderExistsValidator()
          {
              RuleFor(x => x.OrderId).Custom((orderId, context) =>
              {
                  if (GetEnrichment(context).Order is null)
                      context.AddFailure("order not found");
              });
          }
      }

      public class NameValidator : EnrichingValidator<RequestOne>
      {
          public NameValidator()
          {
              RuleFor(x => x.Title).NotEmpty();
          }
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var order = Enrichment.Order;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (generator, errors, files) = Run(source);

    Assert.Empty(errors);
    Assert.Empty(generator.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    var context = files["RequestOneHandler.RequestOneEnrichment.g.cs"];

    // витрина I{Context} собрана из payload'ов энричеров, класс её реализует
    Assert.Contains("public interface IRequestOneEnrichment", context);
    Assert.Contains("global::Order? Order { get; }", context);
    Assert.Contains("global::System.Collections.Generic.IDictionary<string, global::IOrderType> OrdersToPay { get; }", context);
    Assert.Contains("public sealed class RequestOneEnrichment : IRequestOneEnrichment", context);

    var handler = files["RequestOneHandler.Handler.g.cs"];
    Assert.Contains("typeof(global::OrderExistsValidator)", handler);
    Assert.Contains("typeof(global::NameValidator)", handler);
    Assert.Contains("RequestEnricherTypes { get; } = new global::System.Type[] { typeof(global::OrderEnricher) };", handler);
    Assert.Contains("CreateRequestContext() => Enrichment;", handler);
  }

  [Fact]
  public void HandWrittenNeedsInterface_ReportsENR006()
  {
    var source = Model + """

      public interface IOrderAware
      {
          Order Order { get; }
      }

      public class OrderEnricher : Enricher<RequestOne, (Order Order, int Count)>
      {
          protected override ValueTask<(Order, int)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class OrderExistsValidator : EnrichingValidator<RequestOne, IOrderAware>
      {
          public OrderExistsValidator()
          {
              RuleFor(x => x.Title).NotEmpty();
          }
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var order = Enrichment.Order;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (generator, errors, _) = Run(source);

    Assert.Empty(errors);
    Assert.Contains(generator, static diagnostic => diagnostic.Id == "ENR006");
  }

  [Fact]
  public void ContextualValidator_NonInterfaceNeeds_ReportsENR006()
  {
    var source = Model + """

      public class OrderAwareValidator : EnrichingValidator<RequestOne, Order>
      {
          public OrderAwareValidator()
          {
              RuleFor(x => x.Title).NotEmpty();
          }
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
              => Task.FromResult(new ResponseOne());
      }
      """;

    var (generator, errors, _) = Run(source);

    Assert.Empty(errors);
    Assert.Contains(generator, static diagnostic => diagnostic.Id == "ENR006");
  }

  [Fact]
  public void ResponseSide_EnrichersAndContextualValidator_GetResponseEnrichment()
  {
    var source = Model + """

      public class ResponsePayloadEnricher : Enricher<ResponseOne, (ResponseOne Response, string Note)>
      {
          protected override ValueTask<(ResponseOne, string)> LoadAsync(ResponseOne response, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class NoteValidator : EnrichingValidator<ResponseOne, IRequestOneResponseEnrichment>
      {
          public NoteValidator()
          {
              RuleFor(x => x.Echo).Custom((echo, context) =>
              {
                  if (string.IsNullOrEmpty(GetEnrichment(context).Note))
                      context.AddFailure("note missing");
              });
          }
      }

      public class NoneResponseValidator : EnrichingValidator<ResponseOne>
      {
          public NoneResponseValidator()
          {
              RuleFor(x => x.Echo).NotEmpty();
          }
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var note = ResponseEnrichment.Note;
              var echo = ResponseEnrichment.Response;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (generator, errors, files) = Run(source);

    Assert.Empty(errors);
    Assert.Empty(generator.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    var responseContext = files["RequestOneHandler.RequestOneResponseEnrichment.g.cs"];
    Assert.Contains("public interface IRequestOneResponseEnrichment", responseContext);
    Assert.Contains("string Note { get; }", responseContext);
    Assert.Contains("public sealed class RequestOneResponseEnrichment : IRequestOneResponseEnrichment", responseContext);
    Assert.Contains("public global::ResponseOne Response =>", responseContext);
    Assert.Contains("public string Note =>", responseContext);

    var handler = files["RequestOneHandler.Handler.g.cs"];
    Assert.Contains("ResponseEnrichment", handler);
    Assert.Contains("typeof(global::NoneResponseValidator)", handler);
    Assert.Contains("typeof(global::NoteValidator)", handler);
    Assert.Contains("CreateResponseContext() => ResponseEnrichment;", handler);
  }

  [Fact]
  public void PayloadlessEnricher_ImplementsBaseInterface_OnlyTrackedInGuard()
  {
    var source = Model + """

      public class RawEnricher : IDataEnricher<RequestOne>
      {
          public ValueTask<object?> EnrichAsync(RequestOne request, CancellationToken cancellationToken)
              => ValueTask.FromResult<object?>(null);
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
              => Task.FromResult(new ResponseOne());
      }
      """;

    var (_, errors, files) = Run(source);

    Assert.Empty(errors);

    Assert.False(files.ContainsKey("RequestOneHandler.RequestOneEnrichment.g.cs"));

    var handler = files["RequestOneHandler.Handler.g.cs"];
    Assert.Contains("RequestEnricherTypes { get; } = new global::System.Type[] { typeof(global::RawEnricher) };", handler);
  }

  [Fact]
  public void ContextNameOverride_Attribute_RenamesContextClassAndInterface()
  {
    var source = Model + """

      public class OrderEnricher : Enricher<RequestOne, (Order Order, int Count)>
      {
          protected override ValueTask<(Order, int)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class OrderExistsValidator : EnrichingValidator<RequestOne, ISalesContext>
      {
          public OrderExistsValidator()
          {
              RuleFor(x => x.OrderId).Custom((orderId, context) =>
              {
                  if (GetEnrichment(context).Order is null)
                      context.AddFailure("order not found");
              });
          }
      }

      [EnrichmentContext(Request = "SalesContext")]
      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var order = Enrichment.Order;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (generator, errors, files) = Run(source);

    Assert.Empty(errors);
    Assert.Empty(generator.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    Assert.False(files.ContainsKey("RequestOneHandler.RequestOneEnrichment.g.cs"));

    var context = files["RequestOneHandler.SalesContext.g.cs"];
    Assert.Contains("public interface ISalesContext", context);
    Assert.Contains("Order Order { get; }", context);
    Assert.Contains("public sealed class SalesContext : ISalesContext", context);

    var handler = files["RequestOneHandler.Handler.g.cs"];
    Assert.Contains("protected SalesContext Enrichment => _enrichment ??= new SalesContext(RequestBag);", handler);
    Assert.Contains("CreateRequestContext() => Enrichment;", handler);
    Assert.Contains("typeof(global::OrderExistsValidator)", handler);
  }

  [Fact]
  public void ContextNameOverride_Collides_ReportsENR007()
  {
    var source = Model + """

      public class RequestOneEnricher : Enricher<RequestOne, (Order Order, int Count)>
      {
          protected override ValueTask<(Order, int)> LoadAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class ResponseEnricher : Enricher<ResponseOne, (string Note, int Ignored)>
      {
          protected override ValueTask<(string, int)> LoadAsync(ResponseOne response, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      [EnrichmentContext(Request = "Shared", Response = "Shared")]
      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
      """ + HandlerCtor + """

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var order = Enrichment.Order;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (generator, _, files) = Run(source);

    Assert.Contains(generator, static diagnostic => diagnostic.Id == "ENR007");

    // имя взято один раз: второй борт не переизменяет файл
    Assert.True(files.ContainsKey("RequestOneHandler.Shared.g.cs"));
  }
}