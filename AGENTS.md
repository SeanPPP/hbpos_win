# HBPOS Agent Instructions

## Layering Rules

- ViewModels are responsible only for UI state, command binding, navigation triggers, display text, visibility, and command state refresh.
- Business logic belongs in services. ViewModels must not directly access databases, HTTP clients, SQLite, SqlSugar, file system APIs, or repository implementations.
- ViewModels may depend on service interfaces, session/UI models, and pure UI abstractions. They should not depend directly on repositories or API clients.
- Services orchestrate business workflows. Repositories only read and write local data. API clients only perform remote calls.
- Keep dependency direction one way: View -> ViewModel -> Service -> Repository/API client.
- Centralize dependency injection registrations. API registrations belong in `ServiceRegistration`; WPF registrations belong in the client service registration entry point.
- When moving or adding business behavior, add service-level tests. ViewModel tests should focus on command behavior, state mapping, navigation, and UI result mapping.
- Avoid broad rewrites. Migrate one business slice at a time and keep existing behavior covered by tests.
