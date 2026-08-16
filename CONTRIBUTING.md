# Contributing to SwBridge

Issues and pull requests are welcome. This project is maintained on a best-effort basis — no
particular response time is promised, but reports and contributions are genuinely useful.

## Before you file an issue

- Say which SOLIDWORKS version you tested against (SwBridge is late-bound almost everywhere, so
  reports of what works — or doesn't — on versions other than the one this repo tests against are
  especially useful; see README's Requirements section).
- Include the smallest reproduction you can, ideally as a snippet against `SwBridge.Sample`.

## Building and testing

```bash
dotnet build SwBridge.sln
dotnet test SwBridge.sln
dotnet run --project samples/SwBridge.Sample    # requires a running SolidWorks instance
```

## What a pull request should look like

- **Tests in `tests/SwBridge.Tests` must run without SolidWorks installed.** If your change needs
  a live SolidWorks instance to verify (most write-path and discovery behavior does), that
  verification belongs in `samples/SwBridge.Sample` instead — extend it behind an opt-in flag
  rather than making the default run touch a live document, and don't leave scratch documents
  open when it's done.
- Keep the build at zero warnings; public members need XML doc comments (this ships as a NuGet
  package — the docs are the product):
  ```bash
  dotnet build SwBridge.sln   # should report 0 Warning(s)
  ```
- SwBridge is mechanism, not policy. This library contains no application-specific integration
  code, no AI/agent framework code, and no business logic (licensing, subscriptions, telemetry,
  or otherwise). If a change would be useless to someone building an unrelated SolidWorks tool,
  it belongs in a consuming application, not here.
- Update `README.md`/`CLAUDE.md` when behavior changes, and `CHANGELOG.md` for anything
  user-visible.

## License

By contributing, you agree your contribution is licensed under this project's [MIT license](LICENSE).
