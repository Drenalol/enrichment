using System.Collections.Immutable;

namespace Enrichment.Generator;

// Модели — иммутабельные записи со строковыми ключами: не держат Compilation/Symbol,
// поэтому value-based equality корректно инвалидирует инкрементальный кэш.

internal sealed record OuterType(string Name, string Accessibility, ImmutableArray<string> TypeParameters);

internal sealed record HandlerInfo(
  string? Namespace,
  string Name,
  string Accessibility,
  ImmutableArray<OuterType> OuterTypes,
  string RequestKey,
  string ResponseKey,
  string? RequestContextName,
  string? ResponseContextName
);

// Единый скан сборки: payload'-энричеры и валидаторы (контекстные помечены Contextual).
internal sealed record ScanResult(
  ImmutableArray<EnricherInfo> Enrichers,
  ImmutableArray<ValidatorInfo> Validators
);

// Одна запись = одна пара (энричер, TReq). payload не развёрнут: PayloadDisplay == null, Members пуст.
internal sealed record EnricherInfo(
  string EnricherTypeFq,
  string RequestKey,
  bool IsAccessible,
  string? PayloadDisplay,
  ImmutableArray<FlatMember> Members,
  ImmutableArray<ValidatorDiagnostic> Diagnostics
);

// Одна запись = одна пара (валидатор, TReq). Contextual — исполняется после энричеров.
internal sealed record ValidatorInfo(
  string ValidatorTypeFq,
  string RequestKey,
  bool IsAccessible,
  bool Contextual,
  ImmutableArray<ValidatorDiagnostic> Diagnostics
);

internal sealed record FlatMember(string Name, string TypeDisplay, string Accessor);

internal sealed record ValidatorDiagnostic(string Id, string[] Args);