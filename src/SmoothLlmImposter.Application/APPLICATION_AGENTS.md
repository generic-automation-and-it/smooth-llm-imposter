# APPLICATION_AGENTS.md

## TL;DR

Vertical-slice use cases dispatched via the Mediator source generator — one folder per feature under `Features/`, never a horizontal `Commands/`/`Queries/` split.

## Non-Negotiables

- **No `Commands/` or `Queries/` folders.** Each use case lives in `Features/<FeatureName>/` with its request, response, validator, and handler colocated.
- **No domain logic here.** Application orchestrates Domain and infrastructure abstractions; business rules live in Domain.
- **Define infrastructure contracts as interfaces here.** Persistence store contracts go in `Common/Persistence/`, external client contracts in `Common/Clients/`. Application depends on `IFoo`, never on an Infrastructure concrete type.
- **Use `Mediator` (martinothamar), not `MediatR`.** Register handlers as `Scoped` (`MediatorOptions.ServiceLifetime = ServiceLifetime.Scoped`) so they can consume scoped collaborators such as `DbContext`; the generator defaults to Singleton, which fails DI scope validation.
- **FluentValidation runs fail-fast** in a Mediator pipeline behavior under `Common/Pipelines/`, before the handler.
- **References Domain only** — never Infrastructure or Host.

## Slice shape (`Features/<Name>/<UseCase>.cs`)

```text
Features/
  <FeatureName>/
    <UseCase>.cs
      ├ Request    : IRequest<Response>
      ├ Response
      ├ Validator  : AbstractValidator<Request>
      └ Handler    : IRequestHandler<Request, Response>
```

## Packages to add when implementing

`Mediator.Abstractions` + `Mediator.SourceGenerator`, `FluentValidation` (core only — validators are registered explicitly, one line per request in `DependencyInjection`, because the `FluentValidation.DependencyInjectionExtensions` assembly scanner is trim-unsafe), and (for upstream clients) `Refit` — all declared centrally in `Directory.Packages.props`. `ValidatorRegistrationTests` fails when a `Request` gains a `Validator` without a registration line.

## Managed GitHub Copilot feature (HLD 013)

`Features/GithubCopilot/` owns the device-login state machine, the published-connection snapshot, the publisher,
startup hydration, refresh and the admin slices. The store, protector and GitHub clients are Application-owned
interfaces implemented in Infrastructure. Keep every admin request registered in `DependencyInjection` with an
explicit validator (the `ValidatorRegistrationTests` drift guard fails otherwise), and keep the login flow
bounded/in-memory — no durable messaging, queue or reconcile endpoint.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-05-30 | Created — empty vertical-slice skeleton (`Features/`, `Common/{Clients,Exceptions,Models,Persistence,Pipelines}/`, `Extensions/`). | — |
| 2026-10-17 | NativeAOT support: validators registered explicitly in `DependencyInjection` (assembly scanning is trim-unsafe); `JsonArray.Add(JsonObject)` sites cast to `JsonNode` (same overload, no generic code-gen); `ValidatorRegistrationTests` guards the registration list. | — |
| 2026-10-20 | GitHub Copilot slices + snapshot/publisher/startup/refresh registrations; reserved-key ownership consulted by provider CRUD. | #126 |
