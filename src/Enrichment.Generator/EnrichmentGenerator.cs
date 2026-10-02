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
/// Плоский Enrichment хендлера строится из payload'ов всех валидаторов:
/// хендлер объявляет только base EnrichedHandler&lt;TRequest, TResponse&gt; (и partial),
/// валидаторы — только IEnrichingValidator&lt;TRequest, TData&gt;; атрибуты не нужны.
/// TData разворачивается: именованный кортеж — по элементам, класс/record — по публичным свойствам.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EnrichmentGenerator : IIncrementalGenerator
{
  private const string RuntimeNamespace = "Enrichment";
  private const string HandlerSimpleName = "EnrichedHandler";
  private const string ValidatorSimpleName = "IEnrichingValidator";

  private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

  /// <summary>
  /// Строит инкрементальный пайплайн: провайдер хендлеров (по base-списку <c>EnrichedHandler&lt;,&gt;</c>),
  /// единый скан сборки на реализацию <c>IEnrichingValidator&lt;,&gt;</c> и эмит
  /// <c>*Enrichment.g.cs</c> / <c>*ResponseEnrichment.g.cs</c> / <c>*.Handler.g.cs</c> на каждый хендлер.
  /// </summary>
  public void Initialize(IncrementalGeneratorInitializationContext context)
  {
    var handlers = context.SyntaxProvider
        .CreateSyntaxProvider(IsCandidateHandler, GetHandler)
        .Where(static handler => handler is not null)
        .Select(static (handler, _) => handler!)
        .Collect();

    var validators = context.CompilationProvider.Select(GetValidators);

    context.RegisterSourceOutput(handlers.Combine(validators), static (production, pair) =>
    {
      foreach (var handler in pair.Item1)
        EmitHandler(production, handler, pair.Item2);
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

    return new HandlerInfo(
        symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
        symbol.Name,
        AccessibilityKeyword(symbol.DeclaredAccessibility),
        outerTypes.ToImmutable(),
        typeArguments[0].ToDisplayString(FullyQualified),
        typeArguments[1].ToDisplayString(FullyQualified));
  }

  // ──────────────────────────── Validators ────────────────────────────

  private static ImmutableArray<ValidatorInfo> GetValidators(Compilation compilation, CancellationToken cancellationToken)
  {
    var builder = ImmutableArray.CreateBuilder<ValidatorInfo>();

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

        CollectValidator(type, builder);
      }
    }

    return builder.ToImmutable();
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

    var payloadInterfaces = validatorInterfaces.Where(static i => i.TypeArguments.Length == 2).ToLookup(static i => i.TypeArguments[0].ToDisplayString(FullyQualified));

    foreach (var group in payloadInterfaces)
    {
      var diagnostics = ImmutableArray.CreateBuilder<ValidatorDiagnostic>();

      if (group.Count() > 1)
      {
        builder.Add(new ValidatorInfo(
            validatorTypeFq,
            group.Key,
            isAccessible,
            null,
            ImmutableArray<FlatMember>.Empty,
            ImmutableArray.Create(new ValidatorDiagnostic("ENR004", [validatorTypeFq, group.Key]))));
        continue;
      }

      var payload = group.Single().TypeArguments[1];
      var members = ImmutableArray.CreateBuilder<FlatMember>();
      var payloadDisplay = Display(payload);

      ExpandPayload(payload, string.Empty, members, diagnostics);

      if (diagnostics.Count > 0)
      {
        builder.Add(new ValidatorInfo(validatorTypeFq, group.Key, isAccessible, null, ImmutableArray<FlatMember>.Empty, diagnostics.ToImmutable()));
        continue;
      }

      builder.Add(new ValidatorInfo(validatorTypeFq, group.Key, isAccessible, payloadDisplay, members.ToImmutable(), ImmutableArray<ValidatorDiagnostic>.Empty));
    }

    if (payloadInterfaces.Count == 0 && validatorInterfaces.Any(static i => i.TypeArguments.Length == 1))
      builder.Add(new ValidatorInfo(validatorTypeFq, validatorInterfaces.First(static i => i.TypeArguments.Length == 1).TypeArguments[0].ToDisplayString(FullyQualified), isAccessible, null, ImmutableArray<FlatMember>.Empty, ImmutableArray<ValidatorDiagnostic>.Empty));
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

  private static void EmitHandler(SourceProductionContext production, HandlerInfo handler, ImmutableArray<ValidatorInfo> validators)
  {
    var requestSide = ResolveSide(production, handler, validators, handler.RequestKey);
    var responseSide = ResolveSide(production, handler, validators, handler.ResponseKey);

    if (requestSide.Validators.Count == 0 && responseSide.Validators.Count == 0)
      return;

    var requestContext = ContextName(handler.Name, string.Empty);
    var responseContext = ContextName(handler.Name, "Response");

    production.AddSource($"{handler.Name}.{requestContext}.g.cs", ContextText(handler.Namespace, requestContext, requestSide));
    production.AddSource($"{handler.Name}.{responseContext}.g.cs", ContextText(handler.Namespace, responseContext, responseSide));
    production.AddSource($"{handler.Name}.Handler.g.cs", HandlerText(handler, requestContext, requestSide, responseContext, responseSide));
  }

  private sealed record SideMember(FlatMember Member, string ValidatorExpression, string PayloadDisplay);

  private sealed class Side
  {
    public List<SideMember> Members = [];
    public readonly List<string> Validators = []; // typeof(...) выражения, включая None-валидаторы
  }

  private static Side ResolveSide(SourceProductionContext production, HandlerInfo handler, ImmutableArray<ValidatorInfo> validators, string key)
  {
    var side = new Side();
    var ownerByName = new Dictionary<string, string>(StringComparer.Ordinal);
    var duplicated = new HashSet<string>(StringComparer.Ordinal);

    foreach (var validator in validators)
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

      var validatorExpression = $"typeof({validator.ValidatorTypeFq})";
      side.Validators.Add(validatorExpression);

      if (validator.PayloadDisplay is null)
        continue;

      foreach (var member in validator.Members)
      {
        if (ownerByName.TryGetValue(member.Name, out var owner))
        {
          duplicated.Add(member.Name);
          production.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicateEnrichmentMember, Location.None, member.Name, owner + " and " + validator.ValidatorTypeFq));
          continue;
        }

        ownerByName[member.Name] = validator.ValidatorTypeFq;
        side.Members.Add(new SideMember(member, validatorExpression, validator.PayloadDisplay));
      }
    }

    side.Members = [.. side.Members.Where(sideMember => !duplicated.Contains(sideMember.Member.Name))];
    return side;
  }

  private static DiagnosticDescriptor Descriptor(string id) => id switch
  {
    "ENR001" => Diagnostics.PayloadNotExpandable,
    "ENR002" => Diagnostics.DuplicateEnrichmentMember,
    "ENR004" => Diagnostics.ConflictingPayloads,
    _ => Diagnostics.ValidatorNotAccessible,
  };

  private static string ContextName(string handlerName, string side) =>
      (handlerName.EndsWith("Handler", StringComparison.Ordinal) ? handlerName.Substring(0, handlerName.Length - "Handler".Length) : handlerName) + side + "Enrichment";

  private static string HandlerText(HandlerInfo handler, string requestContext, Side requestSide, string responseContext, Side responseSide)
  {
    var text = new StringBuilder();
    Open(text, handler);

    AppendContextProperty(text, handler, requestContext, requestSide, "RequestBag", "Enrichment", "Request");
    AppendContextProperty(text, handler, responseContext, responseSide, "ResponseBag", "ResponseEnrichment", "Response");

    text.AppendLine("}");

    foreach (var _ in handler.OuterTypes)
      text.AppendLine("}");

    return text.ToString();
  }

  private static void AppendContextProperty(StringBuilder text, HandlerInfo handler, string contextName, Side side, string bagField, string propertyName, string typesProperty)
  {
    var qualified = handler.Namespace is null ? contextName : $"global::{handler.Namespace}.{contextName}";

    if (side.Validators.Count == 0)
      return;

    var field = "_" + char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
    text.AppendLine($"  private {qualified}? {field};");
    text.AppendLine($"  protected {qualified} {propertyName} => {field} ??= new {qualified}({bagField});");
    text.AppendLine($"  protected override global::System.Type[] {typesProperty}ValidatorTypes {{ get; }} = new global::System.Type[] {{ {string.Join(", ", side.Validators)} }};");
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

    text.AppendLine($"public sealed class {name}");
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
      text.AppendLine($"  public {member.TypeDisplay} {member.Name} => Get({sideMember.ValidatorExpression}) is {sideMember.PayloadDisplay} p ? p.{member.Accessor} : throw Missing({sideMember.ValidatorExpression}, \"{member.Name}\");");
    }

    text.AppendLine();
    text.AppendLine("  private object? Get(global::System.Type validator)");
    text.AppendLine("    => _bag.TryGetValue(validator, out var payload) ? payload : throw Missing(validator, \"?\");");
    text.AppendLine();
    text.AppendLine("  private static global::System.Exception Missing(global::System.Type validator, string member)");
    text.AppendLine("    => new global::System.InvalidOperationException($\"Validator '{validator}' did not provide a payload for Enrichment member '{member}'. It did not run (validation stopped before it), is not registered in DI, returned a null payload, or the generated sources are stale and need a rebuild.\");");
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