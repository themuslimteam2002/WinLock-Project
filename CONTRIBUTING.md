# Contributing to AppGuardian

Thank you for your interest in contributing to AppGuardian. This document explains how to set up the project, report issues, and submit changes.

## Code of Conduct

This project follows a simple rule: be respectful. Technical disagreements are welcome; personal attacks are not.

## How to Contribute

### Reporting Bugs

1. Check [existing issues](../../issues) to avoid duplicates
2. Use the [bug report template](../../issues/new?template=bug_report.md)
3. Include:
   - Windows version (`winver`)
   - Steps to reproduce
   - Expected vs. actual behavior
   - Diagnostic output (see [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md#log-files--diagnostics))

### Suggesting Features

1. Check [existing issues](../../issues) for prior discussion
2. Use the [feature request template](../../issues/new?template=feature_request.md)
3. Explain the use case, not just the solution

### Submitting Code

1. Fork the repository
2. Create a feature branch from `main`: `git checkout -b feature/your-feature`
3. Make your changes
4. Ensure the build passes: `dotnet build -c Release`
5. Ensure tests pass: `dotnet test`
6. Commit with a clear message (see [Commit Messages](#commit-messages))
7. Open a pull request against `main`

## Development Setup

### Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 8.0+ | [Download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Windows | 10 22H2+ or 11 | Required for UI, Agent, Service projects |
| Inno Setup | 6.0+ | Only needed to build the installer |
| IDE | Visual Studio 2022 or VS Code | Rider also works |

### Getting Started

```powershell
# Clone
git clone https://github.com/hafiz/appguardian.git
cd appguardian

# Restore + build
dotnet restore
dotnet build -c Release

# Run tests
dotnet test

# Run the dashboard (for UI work)
dotnet run --project src/AppGuardian.UI
```

### Project Architecture

```
AppGuardian.UI        → WPF dashboard, MVVM, no business logic
AppGuardian.Agent     → User-session process: overlays, Hello, VD, hooks
AppGuardian.Service   → Windows Service: policy store, process monitor, enforcement
AppGuardian.Shared    → Models, IPC contracts, DTOs, interfaces
AppGuardian.Tests     → Unit and integration tests
```

Key documents:
- [SRS.md](docs/SRS.md) — requirements
- [API_DESIGN.md](docs/API_DESIGN.md) — IPC contracts
- [DECISIONS.md](docs/DECISIONS.md) — architecture decisions

### Testing

```powershell
# All tests
dotnet test

# Specific project
dotnet test tests/AppGuardian.Tests

# With coverage
dotnet test --collect:"XPlat Code Coverage"
```

## Code Style

### C# Conventions
- **Language version:** C# 12
- **Nullable reference types:** enabled globally
- **Implicit usings:** enabled
- **Naming:** PascalCase for public members, `_camelCase` for private fields
- **Async:** suffix `Async` on async methods
- **Comments:** explain *why*, not *what* — the code should explain itself

### XAML Conventions
- Use `DynamicResource` for theme colors (supports runtime theme switching)
- Use `StaticResource` for theme-agnostic tokens (spacing, radius, icons)
- Name all interactive elements with `AutomationProperties.Name`
- Use design system styles — avoid inline colors, sizes, or fonts

### Commit Messages

Use clear, imperative-mood messages:

```
Add Windows Hello verification flow

- Implement KeyCredentialManager check
- Add fallback to UserConsentVerifier
- Wire up IPC auth.verifyHello endpoint
- FR-301
```

Format: `<verb> <what>`, optionally with bullet points and SRS trace IDs.

## Pull Request Process

1. **One PR per concern.** Don't mix bug fixes with feature work.
2. **Include context.** Explain what the change does and why.
3. **Reference issues.** Link to the relevant issue: `Fixes #42`.
4. **Pass CI.** The build must succeed.
5. **Tests.** Add or update tests for behavioral changes.
6. **No new warnings.** Build with warnings-as-errors for nullable.

### PR Template

A pull request template is provided at `.github/PULL_REQUEST_TEMPLATE.md`.

## Branching

| Branch | Purpose |
|---|---|
| `main` | Stable, release-ready code |
| `feature/*` | New features |
| `fix/*` | Bug fixes |
| `docs/*` | Documentation changes |

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).

## Questions?

Open a [discussion](../../discussions) or file an issue. There is no Slack, Discord, or mailing list — everything happens on GitHub.
