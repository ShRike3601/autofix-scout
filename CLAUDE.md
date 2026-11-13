# AutoFix Scout

Read `docs/HANDOFF.md` first: it holds the state of the work, the decisions made and what's next.

## Machine safety rule (read before running anything)

This app changes drivers and devices. Before running the app, the executor (`AutoFixScout.Executor.exe`),
`pnputil`, driver installs, device disable/enable or service restarts, run `hostname`:

- `AFS-TESTBENCH`: the spare test laptop. Risky testing is allowed here.
- Anything else (including `DESKTOP-A9TLII5`, the owner's daily PC): **build and unit tests only**.
  Do not run the app or the executor, and do not change drivers, devices or services. Ask the owner first.

## Build and test

```powershell
dotnet build AutoFixScout.sln
dotnet test AutoFixScout.sln      # blocked on machines with Smart App Control on; CI runs them
```

CI (GitHub Actions, `.github/workflows/build.yml`) builds and tests on Windows and uploads the app as an artifact.
