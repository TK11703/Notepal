# Project Guidelines

## Architecture

- Target C# 14 and .NET 10.
- Preserve the existing multi-project architecture and dependency direction.
- Organize Minimal API functionality as vertical feature slices.
- Put shared request and response contracts in `Notepal.Contracts`.
- Keep HTTP, authentication, authorization, and orchestration in `Notepal.Api`.
- Keep SQL migrations and Dapper persistence in `Notepal.Database`.
- Keep UI behavior and presentation in `Notepal.Web`.
- Reuse existing abstractions and patterns before introducing new dependencies.
- Do not introduce another ORM alongside Dapper.

## C# and .NET

- Preserve nullable-reference-type safety; do not suppress warnings without a documented reason.
- Use asynchronous APIs for database, network, and file operations.
- Pass `CancellationToken` through endpoint, service, and database boundaries.
- Do not use `.Result`, `.Wait()`, `async void`, or unnecessary `Task.Run`.
- Prefer dependency injection and small, focused services over static mutable state.
- Use `DateTimeOffset` or UTC consistently for persisted timestamps.
- Validate arguments at system boundaries rather than deep inside domain logic.
- Do not expose database entities directly as API contracts.

## Minimal APIs

- Require authentication and authorization explicitly for protected endpoints.
- Derive the current user or owner from validated authentication claims, never from request data.
- Validate request data before performing database or external-service operations.
- Return appropriate HTTP status codes and consistent `ProblemDetails` responses.
- Use typed results and endpoint metadata when consistent with the existing feature.
- Keep endpoint handlers thin; move substantial business and persistence logic into focused services.
- Do not leak exception messages, SQL details, credentials, or internal implementation details to clients.

## PostgreSQL and Dapper

- Use parameterized Dapper queries exclusively; never construct SQL using untrusted string interpolation.
- Scope owner-specific reads, updates, and deletes by owner ID in the SQL query itself.
- Use explicit column lists; avoid `SELECT *`.
- Use PostgreSQL naming conventions consistently, including `snake_case` identifiers.
- Use transactions for operations that must succeed or fail atomically.
- Pass cancellation tokens to database commands.
- Keep migrations forward-only, versioned, deterministic, and safe for existing data.
- Add appropriate constraints and indexes rather than relying only on application validation.
- Avoid N+1 queries and unnecessary database round trips.
- Make ordering explicit whenever result order affects behavior or tests.

## JavaScript and Bootstrap

- Prefer Bootstrap 5.3 utilities and components over custom CSS or JavaScript when they meet the requirement.
- Do not introduce jQuery or a second UI framework.
- Use semantic HTML and preserve keyboard navigation, focus behavior, and accessible labels.
- Include accessible names for icon-only controls.
- Use Bootstrap validation and accessibility states consistently.
- Keep JavaScript modular and narrowly scoped; avoid global variables and inline scripts.
- For Blazor JavaScript interop, use module-based interop and dispose imported modules and event handlers.
- Do not manipulate DOM owned by a Blazor component unless JavaScript interop is required.
- Escape or safely render user-provided content; do not use `innerHTML` with untrusted values.
- Respect `prefers-reduced-motion` when adding animation.

## Security and Reliability

- Never commit secrets, credentials, connection strings, or production identifiers.
- Store secrets through configuration providers or development user secrets.
- Treat all request, uploaded-file, URL, and external-service data as untrusted.
- Enforce authorization server-side even when the UI hides unauthorized actions.
- Apply sensible limits to uploads, imports, pagination, and collection sizes.
- Use structured logging with message templates.
- Do not log secrets, tokens, complete connection strings, or sensitive personal data.
- Do not silently catch exceptions; handle expected failures explicitly and allow unexpected failures to reach standard error handling.
- External-service failures must not corrupt persisted state.

## Testing and Validation

- Add or update tests for every observable behavior change and regression fix.
- Prefer focused unit or component tests, then add integration or end-to-end coverage where boundaries require it.
- Test successful behavior, validation failures, authorization, owner isolation, and important edge cases.
- Database tests must exercise real PostgreSQL behavior when SQL semantics are relevant.
- Avoid tests that depend on execution order, shared mutable state, arbitrary delays, or external production services.
- Run the smallest relevant test set first, then build the affected solution or projects.
- Do not weaken, skip, or delete tests merely to make validation pass.

## Change Discipline

- Make focused changes and avoid unrelated refactoring.
- Preserve existing public contracts unless the requested change intentionally modifies them.
- Update directly related documentation when behavior, configuration, or operational procedures change.
- Follow existing naming, formatting, endpoint, repository, and testing patterns.
- Do not add a dependency when the platform or an existing package already provides the required behavior.

## Commit Messages

Use Conventional Commits:

- `feat:` for new functionality
- `fix:` for bug fixes
- `docs:` for documentation
- `test:` for tests
- `refactor:` for behavior-preserving restructuring
- `perf:` for performance improvements
- `build:` for build or dependency changes
- `ci:` for CI/CD changes
- `chore:` for maintenance

Write concise imperative subjects without a trailing period.