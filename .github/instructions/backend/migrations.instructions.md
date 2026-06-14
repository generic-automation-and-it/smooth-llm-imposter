---
description: 'Backend migration rules: EF Core migration authoring conventions and code-coverage exclusion requirements'
globs: "src/*.Infrastructure/Persistence/Migrations/**/*.cs"
paths:
  - "src/*.Infrastructure/Persistence/Migrations/**/*.cs"
applyTo: 'src/*.Infrastructure/Persistence/Migrations/**/*.cs'
alwaysApply: false
---

# Backend Migration Rules

Updated: 2026-05-10

## Non-Negotiables

- **Every new migration class must carry `[ExcludeFromCodeCoverage]`.**

  ```csharp
  using System.Diagnostics.CodeAnalysis;
  using Microsoft.EntityFrameworkCore.Migrations;

  namespace Project.Infrastructure.Persistence.Migrations;

  /// <inheritdoc />
  [ExcludeFromCodeCoverage]
  public partial class MyNewMigration : Migration
  {
      // ...
  }
  ```

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
