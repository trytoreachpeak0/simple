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

- SIMPLE is reused across semiconductor plants. Nothing in the product is
  named after a project number or a customer. A plant's business
  configuration lives under `sites/<project>/` (`sites/8005/` first);
  product code reads it as configuration and never branches on which site
  it is. Addresses and credentials are not in it (see Safety).
- Namespaces are `Simple.*`. Do not carry over namespaces or names from
  the previous line.

## Design principles

- **The server is the only brain.** The onboard HMI is a thin terminal: it
  executes commands and reports observed facts (doors, locks, light
  curtains, IO, scans). It keeps only operations that need a person
  physically at the vehicle, and it does not hold its own copy of business
  state.
- **Exceptions come in three tiers:** the server recovers on its own; a
  person handles it remotely in the server console; a person handles it
  on site at the HMI. On-site staff should handle as little as possible.
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
| `src/Simple.Onboard` | Onboard HMI (WPF, `net10.0-windows`) |
| `src/Simple.MesGateway` | MES gateway, a small separate process |
| `src/Simple.IoSimulator` | Simulator for the vehicle's IO module |
| `src/Simple.Protocol` | Server ↔ onboard protocol contracts |
| `tests/` | One `<Project>.Tests` per project, plus `Simple.Contracts.Tests` |
| `scenarios/` | Scenarios in the shared format |
| `samples/replay/` | Anonymized replay samples |
| `sites/<project>/` | Per-site configuration |

Planned, not created yet: `Simple.Cli` (bring the system up and down, run
scenarios) and `Simple.Mcp` (the thin AI layer above).

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

- Never commit real customer data: MES lot and sublot numbers, AREA and
  EQP values, field evidence with real values. Anonymize replay samples,
  or keep them out of the repository.
- Never name plant servers, IP addresses, the customer or colleagues, in
  code, comments, docs, commit messages, issues or PRs.
- Credentials, passwords, keys and tokens never enter the repository.
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
  `10.0.302` with `rollForward: latestFeature`, to be tightened once CI
  has the same SDK. This departs from the older repositories' .NET 8
  baseline (ADR-cross-0056); the exception is recorded in
  `docs/adr/0001-dotnet-10-and-mtp.md`.
- The onboard PCs stay on Windows 10 Pro 22H2, which is not on the .NET 10
  supported-OS list. This is accepted.
- `.slnx` solution format: `simple.slnx`.
- `Directory.Build.props` sets the target framework, nullable, implicit
  usings, warnings as errors and lock files for every project. Do not
  repeat those in a `.csproj`; a WPF project only overrides
  `TargetFramework` to `net10.0-windows`.
- **Central package management.** Every version lives in
  `Directory.Packages.props`. A `PackageReference` never carries
  `Version=`. Commit the `packages.lock.json` files.

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
  (xunit.v3 4.x does not add it on its own). Its `.csproj` only references
  `xunit.v3` and the project under test.
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
  or loopback addresses of fakes appear in the repository.
- `sites/<project>/` holds only non-sensitive site configuration: sites,
  areas, task types, the vehicle list and similar business settings. It
  holds no addresses and no credentials.
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
