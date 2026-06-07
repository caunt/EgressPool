# Repository Guidelines

## Project Structure & Module Organization

This repository is a .NET 10 solution for an outbound egress address pool library.

- `src/EgressPool/`: core library and internal platform implementations.
- `src/EgressPool.DependencyInjection/`: service registration and `IHttpClientFactory` integration.
- `samples/EgressPool.Sample/`: minimal loopback HTTP sample.
- `tests/EgressPool.Tests/`: xUnit tests, including behavioral loopback tests and fake platform helpers.
- `EgressPool.slnx`: solution entry point.

Keep production code in `src/`, test-only helpers in `tests/`, and runnable examples in `samples/`.

## Build, Test, and Development Commands

- `dotnet build EgressPool.slnx`: builds all projects.
- `dotnet test EgressPool.slnx`: runs the full xUnit suite.
- `dotnet test EgressPool.slnx --collect:"XPlat Code Coverage"`: runs tests with Coverlet coverage collection.
- `dotnet run --project samples/EgressPool.Sample/EgressPool.Sample.csproj`: runs the loopback sample.

After verification, remove generated outputs when needed with:

```bash
rm -rf /tmp/EgressPool.Tests tests/EgressPool.Tests/TestResults
find . -type d \( -name bin -o -name obj -o -name .vs \) -prune -exec rm -rf {} \;
```

## Coding Style & Naming Conventions

Use modern C# for `net10.0` with nullable references and implicit usings enabled. Follow the existing style: file-scoped namespaces, primary constructors where they clarify ownership, records for immutable data, and descriptive names. Avoid single-letter identifiers outside trivial indexes. Public APIs should have XML documentation; internal helpers should stay boring and focused.

## Testing Guidelines

Tests use xUnit in `tests/EgressPool.Tests`. Prefer behavioral tests that exercise real public usage over isolated method checks. Use loopback TCP/UDP/HTTP helpers and `FakeEgressNetworkPlatform` for non-privileged, cross-platform coverage. Do not add tests that mutate real OS network configuration unless they are explicitly opt-in and skipped by default.

Name tests as `MethodOrScenario_Condition_ExpectedBehavior`.

## Commit & Pull Request Guidelines

No Git history is available in this workspace. Use concise imperative commit messages, for example `Add HTTP lease lifecycle tests`. Pull requests should include a short summary, test results, and any platform-specific caveats. For networking changes, call out cleanup behavior and whether elevated privileges are required.

## Security & Configuration Tips

Avoid leaving network state behind. Any code that assigns addresses or routes must pair creation with deterministic disposal and stale-state recovery. Keep sample and test prefixes on loopback unless a scenario explicitly requires otherwise.
