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
  public void NamedTuplePayload_FlattensToTypedMembers_AndKeepsNoneValidatorInGuard()
  {
    var source = Model + """

      public class OrderValidator : EnrichingValidator<RequestOne, (Order Order, IDictionary<string, IOrderType> OrdersToPay)>
      {
          public OrderValidator()
          {
              RuleFor(x => x.OrderId).NotEmpty();
          }

          protected override ValueTask<(Order Order, IDictionary<string, IOrderType> OrdersToPay)> EnrichAsync(RequestOne request, CancellationToken cancellationToken)
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
          public RequestOneHandler(IEnumerable<IEnrichingValidator<RequestOne>> requestValidators, IEnumerable<IEnrichingValidator<ResponseOne>> responseValidators) : base(requestValidators, responseValidators)
          {
          }

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

    var handler = files["RequestOneHandler.Handler.g.cs"];
    Assert.Contains("typeof(global::OrderValidator)", handler);
    Assert.Contains("typeof(global::NameValidator)", handler);
  }

  [Fact]
  public void UnnamedTuplePayload_YieldsItemNames()
  {
    var source = Model + """

      public class OrderValidator : EnrichingValidator<RequestOne, (Order, IDictionary<string, IOrderType>)>
      {
          protected override ValueTask<(Order, IDictionary<string, IOrderType>)> EnrichAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
          public RequestOneHandler(IEnumerable<IEnrichingValidator<RequestOne>> requestValidators, IEnumerable<IEnrichingValidator<ResponseOne>> responseValidators) : base(requestValidators, responseValidators)
          {
          }

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
  public void NoneValidator_IsExcludedFromContextMembers_ButTrackedInGuard()
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
          public RequestOneHandler(IEnumerable<IEnrichingValidator<RequestOne>> requestValidators, IEnumerable<IEnrichingValidator<ResponseOne>> responseValidators) : base(requestValidators, responseValidators)
          {
          }

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
              => Task.FromResult(new ResponseOne());
      }
      """;

    var (_, errors, files) = Run(source);

    Assert.Empty(errors);

    var context = files["RequestOneHandler.RequestOneEnrichment.g.cs"];
    Assert.DoesNotContain("=> Get(", context);

    Assert.Contains("typeof(global::NameValidator)", files["RequestOneHandler.Handler.g.cs"]);
  }

  [Fact]
  public void DuplicateMemberNames_ReportError_AndDropMembers()
  {
    var source = Model + """

      public class FirstValidator : EnrichingValidator<RequestOne, (Order Foo, int IgnoredOne)>
      {
          protected override ValueTask<(Order, int)> EnrichAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class SecondValidator : EnrichingValidator<RequestOne, (string Foo, int IgnoredTwo)>
      {
          protected override ValueTask<(string, int)> EnrichAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
          public RequestOneHandler(IEnumerable<IEnrichingValidator<RequestOne>> requestValidators, IEnumerable<IEnrichingValidator<ResponseOne>> responseValidators) : base(requestValidators, responseValidators)
          {
          }

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

      public class StringPayloadValidator : EnrichingValidator<RequestOne, string>
      {
          protected override ValueTask<string> EnrichAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
          public RequestOneHandler(IEnumerable<IEnrichingValidator<RequestOne>> requestValidators, IEnumerable<IEnrichingValidator<ResponseOne>> responseValidators) : base(requestValidators, responseValidators)
          {
          }

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

      public class OrderValidator : EnrichingValidator<RequestOne, (Order Order, int Count)>
      {
          protected override ValueTask<(Order, int)> EnrichAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
          public RequestOneHandler(IEnumerable<IEnrichingValidator<RequestOne>> requestValidators, IEnumerable<IEnrichingValidator<ResponseOne>> responseValidators) : base(requestValidators, responseValidators)
          {
          }

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
              => Task.FromResult(new ResponseOne());
      }
      """;

    var (_, errors, _) = Run(source);

    Assert.Contains(errors, static diagnostic => diagnostic.Id == "CS0260");
  }

  [Fact]
  public void ValidatorFromReferencedAssembly_IsDiscoveredAndTyped()
  {
    var validatorSource = """
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

      public class ReferenceOrderValidator : EnrichingValidator<RequestOne, (Order Order, IDictionary<string, IOrderType> OrdersToPay)>
      {
          protected override ValueTask<(Order Order, IDictionary<string, IOrderType> OrdersToPay)> EnrichAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }
      """;

    var reference = ReferenceOf(validatorSource, "Validators");

    var source = Model + """

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
          public RequestOneHandler(IEnumerable<IEnrichingValidator<RequestOne>> requestValidators, IEnumerable<IEnrichingValidator<ResponseOne>> responseValidators) : base(requestValidators, responseValidators)
          {
          }

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var order = Enrichment.Order;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (_, errors, files) = Run(source, reference.Reference);

    Assert.Empty(errors);
    Assert.Contains("typeof(global::ReferenceOrderValidator)", files["RequestOneHandler.Handler.g.cs"]);
    Assert.Contains("public global::Order Order =>", files["RequestOneHandler.RequestOneEnrichment.g.cs"]);
  }

  [Fact]
  public void NullablePayloadElements_PreserveNullability_AndNullPayloadThrows()
  {
    var source = "#nullable enable\n" + Model + """

      public class NullablePayloadValidator : EnrichingValidator<RequestOne, (Order? Order, decimal? Optional, string Required)>
      {
          protected override ValueTask<(Order?, decimal?, string)> EnrichAsync(RequestOne request, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public partial class RequestOneHandler : EnrichedHandler<RequestOne, ResponseOne>
      {
          public RequestOneHandler(IEnumerable<IEnrichingValidator<RequestOne>> requestValidators, IEnumerable<IEnrichingValidator<ResponseOne>> responseValidators) : base(requestValidators, responseValidators)
          {
          }

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
  public void ResponseValidators_GetSeparateResponseEnrichment()
  {
    var source = Model + """

      public class ResponsePayloadValidator : EnrichingValidator<ResponseOne, (ResponseOne Response, string Note)>
      {
          protected override ValueTask<(ResponseOne, string)> EnrichAsync(ResponseOne response, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
      }

      public class EchoValidator : EnrichingValidator<ResponseOne, ResponseOne>
      {
          protected override ValueTask<ResponseOne> EnrichAsync(ResponseOne response, CancellationToken cancellationToken)
              => throw new System.NotImplementedException();
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
          public RequestOneHandler(IEnumerable<IEnrichingValidator<RequestOne>> requestValidators, IEnumerable<IEnrichingValidator<ResponseOne>> responseValidators) : base(requestValidators, responseValidators)
          {
          }

          protected override Task<ResponseOne> HandleAsync(RequestOne request, CancellationToken cancellationToken)
          {
              var note = ResponseEnrichment.Note;
              var echo = ResponseEnrichment.Echo;
              return Task.FromResult(new ResponseOne());
          }
      }
      """;

    var (generator, errors, files) = Run(source);

    Assert.Empty(errors);
    Assert.Empty(generator.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    var responseContext = files["RequestOneHandler.RequestOneResponseEnrichment.g.cs"];
    Assert.Contains("public global::ResponseOne Response =>", responseContext);
    Assert.Contains("public string Note =>", responseContext);
    Assert.Contains("public string Echo =>", responseContext);

    var handler = files["RequestOneHandler.Handler.g.cs"];
    Assert.Contains("ResponseEnrichment", handler);
    Assert.Contains("typeof(global::NoneResponseValidator)", handler);
  }
}