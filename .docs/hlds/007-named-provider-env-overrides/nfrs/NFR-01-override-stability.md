# NFR-01: Override Stability

**Status:** Prototype

<!-- One file per quality attribute. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

A provider override addresses the **same provider regardless of declaration order**. Reordering,
inserting, or removing other providers in `appsettings.json` MUST NOT change which provider any
override (`Imposter__Providers__<name>__*` or the conventional `<NAME>_*`) targets. There is no
positional (`__N__`) addressing of providers anywhere in the system.

## Verification

L0 unit test: bind an `ImposterOptions` from a configuration whose provider dictionary is built in
two different key orders; assert the resolved `ProviderRoute` for a given name is identical and
that an override for that name lands on it in both orderings. A grep gate / review check confirms
no `Providers__<digit>` pattern remains in source or `.docs/`.

## Acceptance Criteria

- Binding the same providers in any key order yields the same per-name resolved route.
- An override keyed by name applies to that provider after arbitrary reordering of the others.
- No `Imposter__Providers__<index>__` example remains in `appsettings*.json`, source, or docs.

## Applies To

Goal 1 (name-keyed providers); the configuration-binding seam in `ImposterOptions` /
`ProviderCatalog`. Realized by [LADR-01](../ladrs/LADR-01-dictionary-keyed-providers.md).
