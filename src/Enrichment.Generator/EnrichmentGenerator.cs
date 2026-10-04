using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Enrichment.Generator;

/// <summary>
/// Плоский Enrichment хендлера строится из payload'ов всех энричеров слоя данных:
/// хендлер объявляет только base EnrichedHandler&lt;TRequest, TResponse&gt; (и partial),
/// энричеры — IDataEnricher&lt;TRequest, TData&gt;; атрибуты не требуются
/// (опциональный [EnrichmentContext] лишь перебивает имя контекста стороны).
/// TData разворачивается: именованный кортеж — по элементам, класс/record — по публичным свойствам.
/// Генерируемый контекст реализует сгенерированный же интерфейс-витрину I{Context},
/// который контекстные валидаторы и указывают как TNeeds; рукописные needs-интерфейсы
/// (разрешившийся на скане TNeeds) — ошибка ENR006.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EnrichmentGenerator : IIncrementalGenerator
{
  private const string RuntimeNamespace = "Enrichment";
  private const string HandlerSimpleName = "EnrichedHandler";
  private const string ValidatorSimpleName = "IEnrichingValidator";
  private const string EnricherSimpleName = "IDataEnricher";
  private const string ContextAttributeSimpleName = "EnrichmentContextAttribute";

  private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

  /// <summary>
  /// Строит инкрементальный пайплайн: провайдер хендлеров (по base-списку <c>EnrichedHandler&lt;,&gt;</c>),
  /// единый скан сборки на <c>IDataEnricher&lt;,&gt;</c> и <c>IEnrichingValidator&lt;,&gt;</c> и эмит
  /// <c>*Enrichment.g.cs</c> / <c>*ResponseEnrichment.g.cs</c> / <c>*.Handler.g.cs</c> на каждый хендлер.
  /// </summary>
  public void Initialize(IncrementalGeneratorInitializationContext context)
  {
    var handlers = context.SyntaxProvider
        .CreateSyntaxProvider(IsCandidateHandler, GetHandler)
        .Where(static handler => handler is not null)
        .Select(static (handler, _) => handler!)
        .Collect();

    var scan = context.CompilationProvider.Select(GetScan);

    context.RegisterSourceOutput(handlers.Combine(scan), static (production, pair) =>
    {
      var claimed = new Dictionary<(string? Namespace, string Name), string>();

      foreach (var handler in pair.Item1)
        EmitHandler(production, handler, pair.Item2, claimed);
    });
  }

  // ───────────────────────────── Handlers ─────────────────────────────

  private static bool IsCandidateHandler(SyntaxNode node, CancellationToken cancellationToken)
  {
    if (node is not ClassDeclarationSyntax declaration || declaration.BaseList is null)
      return false;

    foreach (var baseType in declaration.BaseList.Types)
    {
      var name = baseType.Type switch
      {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => string.Empty,
      };

      if (name == HandlerSimpleName)
        return true;
    }

    return false;
  }

  private static HandlerInfo? GetHandler(GeneratorSyntaxContext context, CancellationToken cancellationToken)
  {
    var declaration = (ClassDeclarationSyntax)context.Node;

    if (context.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not { } symbol)
      return null;

    var original = symbol.BaseType?.OriginalDefinition;
    if (original is null || original.Name != HandlerSimpleName || original.ContainingNamespace?.ToDisplayString() != RuntimeNamespace)
      return null;

    var typeArguments = symbol.BaseType!.TypeArguments;
    if (typeArguments.Length != 2)
      return null;

    var outerTypes = ImmutableArray.CreateBuilder<OuterType>();
    for (var containing = symbol.ContainingType; containing is not null; containing = containing.ContainingType)
      outerTypes.Add(new OuterType(
          containing.Name,
          AccessibilityKeyword(containing.DeclaredAccessibility),
          containing.TypeParameters.Select(static parameter => parameter.Name).ToImmutableArray()));

    outerTypes.Reverse();

    var (requestContextName, responseContextName) = GetContextNameOverrides(symbol);

    return new HandlerInfo(
        symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
        symbol.Name,
        AccessibilityKeyword(symbol.DeclaredAccessibility),
        outerTypes.ToImmutable(),
        typeArguments[0].ToDisplayString(FullyQualified),
        typeArguments[1].ToDisplayString(FullyQualified),
        requestContextName,
        responseContextName);
  }

  // [EnrichmentContext(Request = "...", Response = "...")] — опциональное переименование
  // контекста стороны; в HandlerInfo уходят просто строки, поэтому incremental-кэш корректен.
  private static (string? Request, string? Response) GetContextNameOverrides(INamedTypeSymbol handler)
  {
    foreach (var attribute in handler.GetAttributes())
    {
      if (attribute.AttributeClass is not { Name: ContextAttributeSimpleName } attributeClass
          || attributeClass.ContainingNamespace?.ToDisplayString() != RuntimeNamespace)
        continue;

      string? request = null;
      string? response = null;

      foreach (var argument in attribute.NamedArguments)
      {
        switch (argument.Key)
        {
          case "Request":
            request = argument.Value.Value as string;
            break;
          case "Response":
            response = argument.Value.Value as string;
            break;
        }
      }

      return (
        string.IsNullOrWhiteSpace(request) ? null : request,
        string.IsNullOrWhiteSpace(response) ? null : response);
    }

    return (null, null);
  }

  // ─────────────────── Enrichers (данные) и Validators (правила) ───────────────────

  private static ScanResult GetScan(Compilation compilation, CancellationToken cancellationToken)
  {
    var enrichers = ImmutableArray.CreateBuilder<EnricherInfo>();
    var validators = ImmutableArray.CreateBuilder<ValidatorInfo>();

    foreach (var assembly in GetAssemblies(compilation))
    {
      cancellationToken.ThrowIfCancellationRequested();

      foreach (var type in EnumerateTypes(assembly.GlobalNamespace))
      {
        cancellationToken.ThrowIfCancellationRequested();

        if (type.IsAbstract || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
          continue;

        if (type.TypeParameters.Length > 0)
          continue;

        if (type.ContainingNamespace?.ToDisplayString() == RuntimeNamespace)
          continue;

        CollectEnricher(type, enrichers);
        CollectValidator(type, validators);
      }
    }

    return new ScanResult(enrichers.ToImmutable(), validators.ToImmutable());
  }

  private static void CollectEnricher(INamedTypeSymbol type, ImmutableArray<EnricherInfo>.Builder builder)
  {
    INamedTypeSymbol[] enricherInterfaces =
    [
      .. type.AllInterfaces
          .Where(static i => i.OriginalDefinition is { Name: EnricherSimpleName } original
                             && original.ContainingNamespace?.ToDisplayString() == RuntimeNamespace),
    ];

    if (enricherInterfaces.Length == 0)
      return;

    var enricherTypeFq = type.ToDisplayString(FullyQualified);
    var isAccessible = IsPublicChain(type);

    var payloadInterfaces = enricherInterfaces.Where(static i => i.TypeArguments.Length == 2).ToLookup(static i => i.TypeArguments[0].ToDisplayString(FullyQualified));

    foreach (var group in payloadInterfaces)
    {
      var diagnostics = ImmutableArray.CreateBuilder<ValidatorDiagnostic>();

      if (group.Count() > 1)
      {
        builder.Add(new EnricherInfo(
            enricherTypeFq,
            group.Key,
            isAccessible,
            null,
            ImmutableArray<FlatMember>.Empty,
            ImmutableArray.Create(new ValidatorDiagnostic("ENR004", [enricherTypeFq, group.Key]))));
        continue;
      }

      var payload = group.Single().TypeArguments[1];
      var members = ImmutableArray.CreateBuilder<FlatMember>();
      var payloadDisplay = Display(payload);

      ExpandPayload(payload, string.Empty, members, diagnostics);

      if (diagnostics.Count > 0)
      {
        builder.Add(new EnricherInfo(enricherTypeFq, group.Key, isAccessible, null, ImmutableArray<FlatMember>.Empty, diagnostics.ToImmutable()));
        continue;
      }

      builder.Add(new EnricherInfo(enricherTypeFq, group.Key, isAccessible, payloadDisplay, members.ToImmutable(), ImmutableArray<ValidatorDiagnostic>.Empty));
    }

    if (payloadInterfaces.Count == 0 && enricherInterfaces.Any(static i => i.TypeArguments.Length == 1))
      builder.Add(new EnricherInfo(enricherTypeFq, enricherInterfaces.First(static i => i.TypeArguments.Length == 1).TypeArguments[0].ToDisplayString(FullyQualified), isAccessible, null, ImmutableArray<FlatMember>.Empty, ImmutableArray<ValidatorDiagnostic>.Empty));
  }

  private static void CollectValidator(INamedTypeSymbol type, ImmutableArray<ValidatorInfo>.Builder builder)
  {
    INamedTypeSymbol[] validatorInterfaces =
    [
      .. type.AllInterfaces
          .Where(static i => i.OriginalDefinition is { Name: ValidatorSimpleName } original
                             && original.ContainingNamespace?.ToDisplayString() == RuntimeNamespace),
    ];

    if (validatorInterfaces.Length == 0)
      return;

    var validatorTypeFq = type.ToDisplayString(FullyQualified);
    var isAccessible = IsPublicChain(type);

    var contextualInterfaces = validatorInterfaces.Where(static i => i.TypeArguments.Length == 2).ToLookup(static i => i.TypeArguments[0].ToDisplayString(FullyQualified));

    var contextualKeys = new HashSet<string>(StringComparer.Ordinal);

    foreach (var group in contextualInterfaces)
    {
      var diagnostics = ImmutableArray.CreateBuilder<ValidatorDiagnostic>();
      var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

      foreach (var validatorInterface in group)
      {
        if (!seen.Add(validatorInterface))
          continue;

        var needsType = validatorInterface.TypeArguments[1];

        // needs — это I{Context}Enrichment, который эмитит этот же генератор: на входе скана
        // он ещё не существует (IErrorTypeSymbol) — это корректная ссылка, её проверит
        // пользовательская компиляция. Разрешившийся тип = рукописная needs — больше не поддерживается.
        if (needsType is not IErrorTypeSymbol)
          diagnostics.Add(new ValidatorDiagnostic("ENR006", [validatorTypeFq, needsType.ToDisplayString(FullyQualified), group.Key]));
      }

      contextualKeys.Add(group.Key);
      builder.Add(new ValidatorInfo(validatorTypeFq, group.Key, isAccessible, true, diagnostics.ToImmutable()));
    }

    // чистые ключи запросов: только guard-массив (контекстные валидаторы на авто-union
    // попадают сюда же, если Roslyn отбросил 2-арный интерфейс с неразрешённым needs)
    foreach (var key in validatorInterfaces.Where(static i => i.TypeArguments.Length == 1).Select(static i => i.TypeArguments[0].ToDisplayString(FullyQualified)).Distinct(StringComparer.Ordinal))
      if (!contextualKeys.Contains(key))
        builder.Add(new ValidatorInfo(validatorTypeFq, key, isAccessible, false, ImmutableArray<ValidatorDiagnostic>.Empty));
  }

  // TData -> плоские члены контекста: кортеж по элементам (вложенные — рекурсивно, доступ ItemN.ItemM),
  // тип — по публичным gettable-свойствам (включая наследуемые).
  private static void ExpandPayload(ITypeSymbol payload, string accessorPrefix, ImmutableArray<FlatMember>.Builder members, ImmutableArray<ValidatorDiagnostic>.Builder diagnostics)
  {
    if (payload.SpecialType != SpecialType.None)
    {
      diagnostics.Add(new ValidatorDiagnostic("ENR001", [DisplayWithAnnotation(payload), string.Empty]));
      return;
    }

    if (payload is INamedTypeSymbol { IsTupleType: true } tuple)
    {
      foreach (var (element, index) in tuple.TupleElements.Select(static (element, index) => (element, index)))
      {
        // 8-й слот — TRest: доступ через Rest.ItemN, имя берём из вложенного элемента.
        if (index >= 7)
        {
          ExpandPayload(element.Type, accessorPrefix + "Rest.", members, diagnostics);
          continue;
        }

        var accessor = accessorPrefix + "Item" + (index + 1);

        if (element.Type is INamedTypeSymbol { IsTupleType: true })
        {
          ExpandPayload(element.Type, accessor + ".", members, diagnostics);
          continue;
        }

        members.Add(new FlatMember(element.Name, DisplayWithAnnotation(element.Type), accessor));
      }

      return;
    }

    var expandable = false;

    for (var type = payload; type is not null && type.SpecialType != SpecialType.System_Object; type = type.BaseType)
    {
      foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
      {
        if (property.IsStatic || property.IsIndexer || property.GetMethod is null || property.DeclaredAccessibility != Accessibility.Public)
          continue;

        members.Add(new FlatMember(property.Name, DisplayWithAnnotation(property.Type), accessorPrefix + property.Name));
        expandable = true;
      }
    }

    if (!expandable)
      diagnostics.Add(new ValidatorDiagnostic("ENR001", [DisplayWithAnnotation(payload), string.Empty]));
  }

  // ─────────────────────────────── Emit ───────────────────────────────

  private static void EmitHandler(
    SourceProductionContext production,
    HandlerInfo handler,
    ScanResult scan,
    Dictionary<(string? Namespace, string Name), string> claimed)
  {
    var requestSide = ResolveSide(production, handler, scan, handler.RequestKey);
    var responseSide = ResolveSide(production, handler, scan, handler.ResponseKey);

    if (IsEmpty(requestSide) && IsEmpty(responseSide))
      return;

    var requestContext = handler.RequestContextName ?? ContextName(handler.Name, string.Empty);
    var responseContext = handler.ResponseContextName ?? ContextName(handler.Name, "Response");

    if (HasContext(requestSide) && ClaimContextName(production, claimed, handler, requestContext))
      production.AddSource($"{handler.Name}.{requestContext}.g.cs", ContextText(handler.Namespace, requestContext, requestSide));

    if (HasContext(responseSide) && ClaimContextName(production, claimed, handler, responseContext))
      production.AddSource($"{handler.Name}.{responseContext}.g.cs", ContextText(handler.Namespace, responseContext, responseSide));

    production.AddSource($"{handler.Name}.Handler.g.cs", HandlerText(handler, requestContext, requestSide, responseContext, responseSide));
  }

  // Имена контекстов уникальны в namespace: [EnrichmentContext] может столкнуть
  // request/response стороны одного хендлера или два хендлера — ловим до компиляции generated-кода.
  private static bool ClaimContextName(
    SourceProductionContext production,
    Dictionary<(string? Namespace, string Name), string> claimed,
    HandlerInfo handler,
    string contextName)
  {
    var key = (handler.Namespace, contextName);

    if (claimed.TryGetValue(key, out var owner))
    {
      production.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicateContextName, Location.None, contextName, owner, handler.Name));
      return false;
    }

    claimed[key] = handler.Name;
    return true;
  }

  private sealed record SideMember(FlatMember Member, string EnricherExpression, string PayloadDisplay);

  private sealed class Side
  {
    public List<SideMember> Members = [];
    public readonly List<string> Enrichers = []; // typeof(...) выражения, включая payload-less энричеры
    public readonly List<string> Validators = []; // typeof(...) выражения: чистые и контекстные
  }

  private static bool IsEmpty(Side side) => side.Enrichers.Count == 0 && side.Validators.Count == 0;

  private static bool HasContext(Side side) => side.Members.Count > 0;

  private static Side ResolveSide(SourceProductionContext production, HandlerInfo handler, ScanResult scan, string key)
  {
    var side = new Side();
    var ownerByName = new Dictionary<string, string>(StringComparer.Ordinal);
    var duplicated = new HashSet<string>(StringComparer.Ordinal);

    foreach (var enricher in scan.Enrichers)
    {
      if (enricher.RequestKey != key)
        continue;

      foreach (var diagnostic in enricher.Diagnostics)
        production.ReportDiagnostic(Diagnostic.Create(Descriptor(diagnostic.Id), Location.None, [.. diagnostic.Args.Cast<object?>()]));

      if (!enricher.IsAccessible)
      {
        production.ReportDiagnostic(Diagnostic.Create(Diagnostics.ValidatorNotAccessible, Location.None, enricher.EnricherTypeFq, $"{handler.Accessibility} {handler.Name}"));
        continue;
      }

      var enricherExpression = $"typeof({enricher.EnricherTypeFq})";
      side.Enrichers.Add(enricherExpression);

      if (enricher.PayloadDisplay is null)
        continue;

      foreach (var member in enricher.Members)
      {
        if (ownerByName.TryGetValue(member.Name, out var owner))
        {
          duplicated.Add(member.Name);
          production.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicateEnrichmentMember, Location.None, member.Name, owner + " and " + enricher.EnricherTypeFq));
          continue;
        }

        ownerByName[member.Name] = enricher.EnricherTypeFq;
        side.Members.Add(new SideMember(member, enricherExpression, enricher.PayloadDisplay));
      }
    }

    side.Members = [.. side.Members.Where(sideMember => !duplicated.Contains(sideMember.Member.Name))];

    foreach (var validator in scan.Validators)
    {
      if (validator.RequestKey != key)
        continue;

      foreach (var diagnostic in validator.Diagnostics)
        production.ReportDiagnostic(Diagnostic.Create(Descriptor(diagnostic.Id), Location.None, [.. diagnostic.Args.Cast<object?>()]));

      if (!validator.IsAccessible)
      {
        production.ReportDiagnostic(Diagnostic.Create(Diagnostics.ValidatorNotAccessible, Location.None, validator.ValidatorTypeFq, $"{handler.Accessibility} {handler.Name}"));
        continue;
      }

      side.Validators.Add($"typeof({validator.ValidatorTypeFq})");
    }

    return side;
  }

  private static DiagnosticDescriptor Descriptor(string id) => id switch
  {
    "ENR001" => Diagnostics.PayloadNotExpandable,
    "ENR002" => Diagnostics.DuplicateEnrichmentMember,
    "ENR004" => Diagnostics.ConflictingPayloads,
    "ENR006" => Diagnostics.NeedsTypeMustBeGenerated,
    _ => Diagnostics.ValidatorNotAccessible,
  };

  private static string ContextName(string handlerName, string side) =>
      (handlerName.EndsWith("Handler", StringComparison.Ordinal) ? handlerName.Substring(0, handlerName.Length - "Handler".Length) : handlerName) + side + "Enrichment";

  private static string HandlerText(HandlerInfo handler, string requestContext, Side requestSide, string responseContext, Side responseSide)
  {
    var text = new StringBuilder();
    Open(text, handler);

    AppendSide(text, handler, requestContext, requestSide, "RequestBag", "Enrichment", "Request");
    AppendSide(text, handler, responseContext, responseSide, "ResponseBag", "ResponseEnrichment", "Response");

    text.AppendLine("}");

    foreach (var _ in handler.OuterTypes)
      text.AppendLine("}");

    return text.ToString();
  }

  private static void AppendSide(StringBuilder text, HandlerInfo handler, string contextName, Side side, string bagField, string propertyName, string sideWord)
  {
    if (IsEmpty(side))
      return;

    text.AppendLine($"  protected override global::System.Type[] {sideWord}EnricherTypes {{ get; }} = new global::System.Type[] {{ {string.Join(", ", side.Enrichers)} }};");
    text.AppendLine($"  protected override global::System.Type[] {sideWord}ValidatorTypes {{ get; }} = new global::System.Type[] {{ {string.Join(", ", side.Validators)} }};");

    if (!HasContext(side))
    {
      text.AppendLine();
      return;
    }

    var qualified = handler.Namespace is null ? contextName : $"global::{handler.Namespace}.{contextName}";

    var field = "_" + char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
    text.AppendLine($"  private {qualified}? {field};");
    text.AppendLine($"  protected {qualified} {propertyName} => {field} ??= new {qualified}({bagField});");
    text.AppendLine($"  protected override global::System.Object? Create{sideWord}Context() => {propertyName};");
    text.AppendLine();
  }

  private static string ContextText(string? ns, string name, Side side)
  {
    var text = new StringBuilder();
    text.AppendLine("// <auto-generated/>");
    text.AppendLine("#nullable enable");
    text.AppendLine();

    if (ns is not null)
    {
      text.AppendLine($"namespace {ns};");
      text.AppendLine();
    }

    // витрина поставки: I{Context} из payload'ов энричеров; контекстные валидаторы
    // ссылаются на неё как TNeeds и получают типизированный доступ к данным
    text.AppendLine($"public interface I{name}");
    text.AppendLine("{");

    foreach (var sideMember in side.Members)
      text.AppendLine($"  {sideMember.Member.TypeDisplay} {sideMember.Member.Name} {{ get; }}");

    text.AppendLine("}");
    text.AppendLine();

    text.AppendLine($"public sealed class {name} : I{name}");
    text.AppendLine("{");
    text.AppendLine("  private readonly global::System.Collections.Generic.Dictionary<global::System.Type, object?> _bag;");
    text.AppendLine();
    text.AppendLine($"  internal {name}(global::System.Collections.Generic.Dictionary<global::System.Type, object?> bag)");
    text.AppendLine("  {");
    text.AppendLine("    _bag = bag;");
    text.AppendLine("  }");
    text.AppendLine();

    foreach (var sideMember in side.Members)
    {
      var member = sideMember.Member;
      text.AppendLine($"  public {member.TypeDisplay} {member.Name} => Get({sideMember.EnricherExpression}, \"{member.Name}\") is {sideMember.PayloadDisplay} p ? p.{member.Accessor} : throw Missing({sideMember.EnricherExpression}, \"{member.Name}\");");
    }

    text.AppendLine();
    text.AppendLine("  private object? Get(global::System.Type enricher, string member)");
    text.AppendLine("    => _bag.TryGetValue(enricher, out var payload) ? payload : throw Missing(enricher, member);");
    text.AppendLine();
    text.AppendLine("  private static global::System.Exception Missing(global::System.Type enricher, string member)");
    text.AppendLine("    => new global::System.InvalidOperationException($\"Enricher '{enricher}' did not provide a payload for Enrichment member '{member}'. It did not run (validation stopped before it), is not registered in DI, returned a null payload, or the generated sources are stale and need a rebuild.\");");
    text.AppendLine("}");

    return text.ToString();
  }

  // ─────────────────────────── Roslyn helpers ───────────────────────────

  private static void Open(StringBuilder text, HandlerInfo handler)
  {
    text.AppendLine("// <auto-generated/>");
    text.AppendLine("#nullable enable");
    text.AppendLine();

    if (handler.Namespace is not null)
    {
      text.AppendLine($"namespace {handler.Namespace};");
      text.AppendLine();
    }

    foreach (var outer in handler.OuterTypes)
      text.AppendLine($"{outer.Accessibility} partial class {outer.Name}{TypeParameterList(outer.TypeParameters)}");

    text.AppendLine($"{handler.Accessibility} partial class {handler.Name}");
    text.AppendLine("{");
  }

  private static string TypeParameterList(ImmutableArray<string> parameters) =>
      parameters.IsEmpty ? string.Empty : "<" + string.Join(", ", parameters) + ">";

  private static string AccessibilityKeyword(Accessibility accessibility) => accessibility switch
  {
    Accessibility.Public => "public",
    Accessibility.Internal => "internal",
    Accessibility.Protected => "protected",
    Accessibility.ProtectedOrInternal => "protected internal",
    Accessibility.ProtectedAndInternal => "private protected",
    _ => "private",
  };

  // Кортежи выводим в канонической форме ValueTuple<...>: (A, B) как type-pattern парсер
  // считает positional-deconstruction, а generic-форма однозначна и компилируется.
  private static string Display(ITypeSymbol type)
  {
    if (type is INamedTypeSymbol { IsTupleType: true } tuple)
      return "global::System.ValueTuple<" + string.Join(", ", tuple.TupleElements.Select(element => Display(element.Type))) + ">";

    return type.ToDisplayString(FullyQualified);
  }

  // Nullable-аннотация ссылочных типов — часть NullableAnnotation, а не имени типа;
  // для value types «?» это сам Nullable<T>, который уже отображается с вопросиком
  // (у него аннотация тоже Annotated —append дал бы «??»).
  // Без этого члена генератор молча стирал Order? в членов Enrichment.
  private static string DisplayWithAnnotation(ITypeSymbol type) =>
      type.NullableAnnotation == NullableAnnotation.Annotated
      && type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }
          ? Display(type) + "?"
          : Display(type);

  private static bool IsPublicChain(ISymbol symbol)
  {
    for (var current = symbol; current is not null; current = current.ContainingSymbol)
    {
      if (current is not ITypeSymbol)
        break;

      if (current.DeclaredAccessibility != Accessibility.Public)
        return false;
    }

    return true;
  }

  private static IEnumerable<IAssemblySymbol> GetAssemblies(Compilation compilation)
  {
    yield return compilation.Assembly;

    foreach (var reference in compilation.References)
    {
      if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly
          && !SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly))
        yield return assembly;
    }
  }

  private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
  {
    foreach (var type in ns.GetTypeMembers())
    {
      foreach (var nested in EnumerateNested(type))
        yield return nested;
    }

    foreach (var child in ns.GetNamespaceMembers())
    {
      foreach (var type in EnumerateTypes(child))
        yield return type;
    }
  }

  private static IEnumerable<INamedTypeSymbol> EnumerateNested(INamedTypeSymbol type)
  {
    yield return type;

    foreach (var nested in type.GetTypeMembers())
    {
      foreach (var deeper in EnumerateNested(nested))
        yield return deeper;
    }
  }
}