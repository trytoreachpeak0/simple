# SIMPLE

**SIMPLE** — **S**upply of **I**ndirect **M**aterials, **P**roduction
**L**ots & **E**quipment parts (辅料、产品批次与机台备件配送系统).
It delivers production lots, indirect materials (machine consumables) and
equipment spare parts to machines in semiconductor plants, driven by the
plant MES, using multi-slot vehicles dispatched through the plant's fleet
manager (RIoT at the first site).

This file holds the product rules. They travel with the code and are
reviewed in pull requests like the code is.

## Product scope

- SIMPLE is reused across semiconductor plants. No project, namespace or
  type in the product is named after a site, a customer or a project
  number. A site's starting business configuration lives under
  `sites/<site>/` (`sites/d8/` first); it is imported into the server at
  first install and used by tests and scenarios, and from then on the
  server holds the live configuration, changed only through its API
  (`docs/adr/0017-site-config-lives-in-server.md`). Product code reads the
  configuration and never branches on which site it is. Endpoints and
  credentials are not in it (see Safety).
- Namespaces are `Simple.*`. Do not carry over namespaces or names from
  the previous line.

## Design principles

- **The server is the only brain.** The onboard terminal is thin: it
  executes commands and reports observed facts (doors, locks, light
  curtains, IO, scans). It keeps only operations that need a person
  physically at the vehicle, and it does not hold its own copy of business
  state.
- **Exceptions come in three tiers:** the server recovers on its own; a
  person handles it remotely in the server console; a person handles it
  on site at the onboard terminal. On-site staff should handle as little
  as possible.
- **The console can do every control.** The goal is that no operation
  needs a script, SSH or database access.
- **Prefer mature libraries to hand-written infrastructure.** Only domain
  logic is ours to write. Hand-writing a general-purpose piece (transport,
  retry, serialization, protocol clients such as Modbus, UI framework
  pieces, scheduling) needs a stated reason in the PR. If a ticket's work
  turns out to be mostly fighting a technology, report that as a finding
  instead of working around it.
- **AI-testability is a product requirement from day one.** An AI session
  must be able to test and fix the system without first building
  infrastructure or learning ad-hoc usage. That takes four things:
  1. one command brings the whole system up and down locally;
  2. one scenario format (initial state, events, expectations) shared by
     CI, people and AI, kept in `scenarios/`;
  3. one observation surface: the server's API and its event record;
  4. one AI-facing guide.

  The end goal is that AI finds a problem, reproduces it as a failing
  scenario, fixes it and opens the PR itself. Build the command line and
  the server API first; an MCP server, if it comes, is a thin layer over
  them and holds no logic of its own. CI, people and AI run the same code
  through different entry points.

## What is in this repository

| Path | Contents |
| --- | --- |
| `src/Simple.Server` | Server host: domain logic, dispatch, API, event record |
| `src/Simple.Server.Console` | Server console; runs in the server process |
| `src/Simple.Server.Riot` | Fleet-manager (RIoT) adapter; runs in the server process |
| `src/Simple.OnboardTerminal` | Onboard terminal (WPF, `net10.0-windows`) |
| `src/Simple.MesGateway` | MES gateway, a small separate process |
| `src/Simple.IoSimulator` | Simulator for the vehicle's IO module |
| `src/Simple.Protocol` | Server ↔ onboard protocol contracts |
| `src/Simple.Cli` | The `simple` command line: brings the local system up and down, shows status, events and logs |
| `tests/` | One `<Project>.Tests` per project, plus `Simple.Contracts.Tests` |
| `scenarios/` | Scenarios in the shared format |
| `samples/replay/` | Replay samples recorded from the real systems |
| `sites/<site>/` | Per-site starting business configuration |

Planned, not created yet: `Simple.Mcp`, the thin AI layer above the command line and the server API.

- **`riot-sdk` is not in this repository.** It stays a separate package
  that other applications can use. `Simple.Server.Riot` consumes it.
- **Fleet-manager behavior is a contract, not a comment.** What has been
  observed about the real fleet manager becomes executable contract tests
  in `Simple.Contracts.Tests`, and any fake fleet manager must pass the
  same tests before other tests may rely on it. Record what the real
  system did as replay samples.
- **The MES gateway is a separate process** that reads the MES today and
  is the single isolation point for writing to the MES later. Nothing else
  talks to the MES.
- **Verdicts and repair**, carried over from earlier test tooling:
  - a check has four outcomes: holds, does not hold, cannot judge, not
    applicable. The last two are deliberately kept apart, and "cannot
    judge" never counts as a pass;
  - automatic repair is authorized in grades;
  - after three failed repairs of the same problem, stop and hand it to a
    person;
  - a `HALT` file stops every automated action.

## Public repository

This repository is public.

- Credentials never enter the repository: passwords, keys, tokens, and
  connection strings that carry any of them. This holds for code,
  configuration, samples, field evidence, docs, commit messages, issues
  and PRs. Strip credentials from a sample or a piece of evidence before
  it goes in.
- Everything else from the field may be public (the product owner,
  2026-10-15): the customer, the site, server names and addresses, real
  lot, sublot, AREA, EQP, station and map values, and field evidence with
  its real values. Replay samples keep their real values.
- Two things stay out regardless: colleagues' names and the systems they
  own, and why the product is called SIMPLE.
- Being allowed to name a real server is not leave to point at it: no
  configuration in this repository, default or opt-in, may carry a real
  endpoint. Real endpoints come from outside the repository (see Safety).
- The README's first paragraph spells the acronym out letter by letter, in
  English and in Chinese. The README does not explain why the name was
  chosen.

## Documentation structure

`CONTEXT.md`, `docs/adr/` and `docs/agents/` are created and maintained by
Matt Pocock's skills (`setup-matt-pocock-skills`, `domain-modeling`;
single-context layout). Do not start a parallel set of these by hand.

## Language

- Agent instruction files are in English: `CLAUDE.md`, everything under
  `.claude/`, `docs/agents/`.
- Everything people read is in Chinese: README body text, documents, issue
  and PR titles and bodies, commit message bodies.
- Commit subjects start with an English conventional prefix (`feat:`,
  `fix:`, `docs:`, `chore:`).
- Inside Chinese text, keep identifiers, paths, commands and error codes
  in English.
- When quoting error or test output, give it in its original language
  first, then explain it.

## Git and pull requests

- Tickets are GitHub issues in this repository.
- Branches are `<type>/<issue>-<slug>`, type one of `feat`, `fix`, `docs`,
  `chore`; for example `feat/12-mes-gateway-poll`.
- Every pull request into `main` gets an independent review before it
  merges.
- Never force push and never rewrite remote history.

## Scripts

- PowerShell 7 only. Every new `.ps1` starts with `#Requires -Version 7`.
- Call `pwsh`, never `powershell.exe`. Do not write Windows PowerShell 5.1
  compatibility code.
- Invoke scripts as `pwsh -File <path>`, with forward slashes in the path.

## Toolchain

- **.NET 10** (LTS until 2028-11-14), pinned in `global.json`:
  `10.0.302` with `rollForward: disable`, so development machines and CI
  need exactly that SDK; the first CI step fails otherwise. Upgrade the
  SDK on development machines and on the CI machine together, in the
  same PR that changes `global.json`. This departs from the older
  repositories' .NET 8 baseline (ADR-cross-0056); the exception is
  recorded in `docs/adr/0001-dotnet-10-and-mtp.md`.
- The onboard PCs stay on Windows 10 Pro 22H2, which is not on the .NET 10
  supported-OS list. This is accepted.
- `.slnx` solution format: `simple.slnx`.
- `Directory.Build.props` sets the target framework, nullable, implicit
  usings, warnings as errors and lock files for every project. Do not
  repeat those in a `.csproj`; a WPF project only overrides
  `TargetFramework` to `net10.0-windows`.
- **Storage is SQL Server 2022 everywhere** (the product owner,
  2026-10-17): LocalDB `(localdb)\MSSQLLocalDB` on development machines,
  Express on `vm01` for CI, never SQLite. The server uses EF Core
  migrations (`src/Simple.Server/Persistence/Migrations`); `dotnet-ef` is a
  local tool pinned in `.config/dotnet-tools.json` (`dotnet tool restore`).
  The database is `Simple_<instance>`, named by `Simple:Instance`; a
  connection string never names a database.
- **Central package management.** Every version lives in
  `Directory.Packages.props`. A `PackageReference` never carries
  `Version=`; restore enforces this with `NU1008`, and with `NU1013` for
  `VersionOverride=`, which `Directory.Packages.props` disables. Commit
  the `packages.lock.json` files: on CI (`CI=true`) restore runs in locked
  mode and fails with `NU1004` when they are out of date.
  `CentralPackageTransitivePinningEnabled` is on: a `PackageVersion` also
  pins that package where it arrives transitively. Raise a vulnerable
  transitive package this way (`NU1902`/`NU1903` fail the build), with a
  comment naming the advisory.

## Local system

- `./simple up` (in PowerShell 7; elsewhere `pwsh -File <clone>/simple.ps1 up`) builds each component's project once, copies the output to `artifacts/simple/<instance>/` and starts every component from there; `./simple down` stops them, `--reset` also drops the instance's database. Every command takes `--json`; the exit codes are fixed and listed in `simple --help`.
- Each component is one entry in `src/Simple.Cli/Components.cs`: project, process count, ports, arguments, readiness. A ticket that adds a component adds its entry there, and `simple up` and `simple status` show it.
- The instance defaults to the clone's folder name, so two worktrees never share one; its database is `Simple_<instance>`. Its state file and `sqlserver.txt` (the SQL Server its database is on) are in `%LOCALAPPDATA%\simple\instances\<instance>\`, or under `SIMPLE_HOME`. The command line stops only the processes it recorded, by process id and start time, never by process name, and stops them gracefully so the server records `server.stopped`.
- The command line starts a local system only. It has no switch that points at anything real, removes `Simple__*`, `ConnectionStrings__*`, `ASPNETCORE_*` and `DOTNET_ENVIRONMENT` from the components' environment, and always runs them as Production.
- SQL Server (`SIMPLE_SQLSERVER`, LocalDB by default): only LocalDB or a named instance on this machine, with Windows authentication. A port, the instance name `MSSQLSERVER`, a failover partner and a SQL login (User ID, Password) are refused. Before starting the server and before dropping a database, the command line asks the server who it is (`SERVERPROPERTY` `IsLocalDB`, `InstanceName`, `MachineName`) and goes on only for LocalDB or a named instance other than `MSSQLSERVER` on this machine.

## Tests

- **xunit.v3 4.x on Microsoft Testing Platform v2.** No VSTest: no
  `Microsoft.NET.Test.Sdk`, no `xunit.runner.visualstudio`, no xunit v2,
  NUnit, MSTest or `coverlet.collector`. `Directory.Build.targets` fails
  the build with `SIMPLE0001` if one appears.
- `global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }`.
  Without it, `dotnet test` on the .NET 10 SDK does not use MTP.
- **Filters use MTP options:** `--filter-class`, `--filter-method`,
  `--filter-trait`, `--filter-query`. Older repositories run on VSTest and
  take `--filter`; that habit does not carry over.

  ```
  dotnet test --solution simple.slnx
  dotnet test --solution simple.slnx --filter-class "*MesGatewayTests"
  ```

- A project whose name ends in `.Tests` is a test project automatically:
  `Directory.Build.props` makes it an executable and adds `using Xunit;`
  (xunit.v3 4.x does not add it on its own). Its `.csproj` references
  `xunit.v3`, the project under test and, where the tests need them,
  test-support packages such as `Microsoft.AspNetCore.Mvc.Testing`; never
  another test framework or a VSTest package (see the list above).
- **Tests that need a database** create their own, `Simple_test_<random>`,
  and drop it when done, so concurrent runs never share one. They use
  LocalDB unless `SIMPLE_TEST_SQLSERVER` holds another SQL Server
  connection string (without a database). Never point it at a shared or
  production instance.
- **Create a test project together with its first test, not before.** A
  test run that finds zero tests exits with code 8 and fails. Never pass
  `--ignore-exit-code 8`: it would also let a filter that selects nothing
  pass, and "nothing ran" is "cannot judge", not a pass.

## Safety

- Anything that moves a vehicle or sends an emergency stop is authorized
  by the product owner separately for every run, with someone on site who
  can reach the physical emergency stop.
- **Default configuration never points at anything real.** No default in
  this repository (`appsettings*.json`, code defaults, scenario defaults,
  install packages) may point at a real fleet manager, a real vehicle or a
  production MES, or enable real work by default. Only placeholder values
  or loopback addresses of fakes appear in these defaults.
- Until the server API has authentication, the server listens on
  loopback only and refuses to start on any other address.
  `Simple.Server.Tests` checks every `appsettings*.json` in the repository
  for hosts that are neither loopback nor LocalDB.
- `sites/<site>/` holds a site's starting business configuration: areas,
  maps, task types, the vehicle list and similar settings, with real
  values. It holds no endpoints the product connects to and no
  credentials.
- Real endpoints and credentials come from outside the repository (a local
  configuration file outside the clone, environment variables, or
  injection at deploy time), and a person selects them explicitly for a
  given run. The previous line shipped defaults that pointed at a
  production vehicle and the production MES with every task type enabled,
  and its only guard was the deploy script: going around the script meant
  going straight at production.

## Agent skills

### Issue tracker

Issues live in this repository's GitHub Issues, used through the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `CONTEXT.md` and one `docs/adr/` at the repository root. See `docs/agents/domain.md`.
