# Testing SIMPLE: the local system, tests and scenarios

How to build SIMPLE, bring it up and down, look into it, run its tests and
scenarios, and turn a problem into a scenario that goes **red** (verdict
`does-not-hold`, exit code 10). Everything below starts from a clone of this
repository; nothing else needs to be read or built first.

Run every command from the clone's root in PowerShell 7 (`pwsh`). `./simple`
is `simple.ps1` at the root: it builds the command line quietly, then runs it.
From another shell, write `pwsh -File <clone>/simple.ps1 <command>`. Every
command takes `--json` (one object `{exitCode, error, result}` on stdout,
progress on stderr), and its exit codes are fixed: `./simple --help` lists them
all, `./simple <command> --help` lists a command's options.

## Where each run goes

The machine you work on may be short of memory or shared with other work, so
heavy work goes to a remote machine through `simple remote`:

| Work | Where |
| --- | --- |
| One test project with a filter (`--project` plus `--filter-class` and the like) | here: `dotnet test --project …` |
| A whole test project, the full test run, a full build | `./simple remote test`, `./simple remote build` |
| Bringing the system up, scenario runs | `./simple remote up`, `./simple remote scenario run …` |
| Reading files, `simple scenario list` and `validate`, `--help` | here |

`simple remote` runs on the machine named by the `SIMPLE_REMOTE` environment
variable, an alias in the user's SSH configuration. Set it in the same
PowerShell command as `simple remote`, since a shell's state may not carry
from one tool call to the next: `$env:SIMPLE_REMOTE = '<alias>'; ./simple remote …`.
The person who gave you the task tells you the alias. Without it every
`simple remote` command ends with 2 and says so. When there is no remote
machine to use, ask whoever gave you the task before running anything heavy
here: on a machine with enough memory to spare, the same commands without
`remote` (`./simple scenario run <name>`, `dotnet test --solution simple.slnx`)
do the same work locally.

What `simple remote` does and what its exit codes mean:

- It sends exactly this clone's working tree, uncommitted changes and new files
  included, as a snapshot commit (`.gitignore` respected, nothing pushed), and
  prints that commit at the start and the end. A file you just wrote is there;
  a file under an ignored folder (`artifacts/`, `bin/`) is not.
- It ends with the remote command's own exit code: a test run's 2 or 8, a
  scenario's verdict. Its own failures:
  - **20**: it did not run (git, ssh or scp failed). Read the message.
  - **21**: the snapshot adds a file over 10 MiB, listed. Delete it or ignore it.
  - **22**: the instance or a slot there stayed busy for 30 minutes (the output
    names the holder). Run the same command again later; never loop on it.
- Output and results come back to `artifacts/remote/<instance>/<time>-<commit>/`,
  whose path the last line prints (`output and results in …`): `output.log`,
  TRX files under `TestResults/`, a scenario's evidence directory, and when a
  test's `simple up` failed, that instance's console output and program logs
  under `test-evidence/` (on CI they are in the uploaded test results).
- Before you delete a clone or worktree, run `./simple remote forget`: it stops
  and drops what this clone left on the remote machine.

## The loop: from a problem to a red scenario

1. **See the system run.** `./simple remote scenario run infrastructure-up`.
   Done when it ends with 0 and its output has the lines
   `Verdict: holds (expected holds, as expected)` and `System stopped …`.
   This proves the remote machine, the build and every component work before
   you change anything. A remote scenario run takes about a minute the first
   time (it builds) and half a minute after that, plus however long it queues
   for a slot (up to 30 minutes): run it in the background or with a timeout
   of 35 minutes or more.
2. **Find the nearest scenario.** `./simple scenario list` lists every scenario
   under `scenarios/`: its name, site, expected verdict and title (what it
   proves); a file that is not valid shows `NOT VALID`. Read the one
   closest to the problem, and `scenarios/README.md` for the format: initial
   state, events (actions through the fakes), expectations, conditions on the
   server's status and event record. The event types an expectation can
   name are the constants in `src/Simple.Server/Events/EventRecord.cs`, each
   with a comment saying when the server records it.
3. **Write the problem as a scenario.** A new file `scenarios/<name>.yaml`,
   first line `# yaml-language-server: $schema=scenario.schema.json`, its
   expectations stating what the system should do. Leave `expected-verdict` out
   (it defaults to `holds`): the scenario states the correct behaviour, and goes
   red while the bug is there.
4. **Validate it.** `./simple scenario validate <name>`. Done when it prints
   `<name>: valid`; otherwise it names the line and column of every problem and
   ends with 2.
5. **Run it and see it go red.** `./simple remote scenario run <name>`. Done
   when it ends with 10 and the `does-not-hold` line names the expectation the
   problem breaks, with the reason the runner saw. Any other ending is not a
   reproduction yet:
   - 0: the scenario does not reach the problem; sharpen the expectation.
   - 11 (cannot tell): an expectation could not be observed, or the scenario
     could not be driven (a component did not come up, an action failed, a
     `when` never came). Read `driveFailure` and each `reason` in `result.json`,
     fix the scenario, run again. Cannot tell is never a pass and never a red.
   - 12 (not applicable): every `only-if` was false.
   - 1: the run could not stop its system (`teardown` in `result.json`).
6. **Fix and go green.** Change the code, run the scenario again until it ends
   with 0, then run the tests the change touches. The scenario goes into the
   same PR as the fix: `dotnet test` and CI run every scenario in `scenarios/`
   (`tests/Simple.Cli.Tests/ScenarioTests.cs`) and fail when its verdict
   differs from its `expected-verdict`. A scenario committed while it is still
   red therefore turns CI red.

A scenario written only to see a red verdict, with no bug behind it (to learn
the format, or to check the runner), is deleted afterwards. The one exception
is a scenario that proves the runner itself judges correctly, such as
`scenarios/deliberately-wrong.yaml`: it says `expected-verdict: does-not-hold`
and has exactly one expectation.

## Scenario runs

`simple scenario run` (here) and `simple remote scenario run` (there) run the
same thing: reset the scenario's own instance (`<instance>-scenario`, apart from
the clone's `simple up`; so a clone's own instance never ends in `-scenario`,
and in a folder named that way every command ends with 2 until you rename the
folder or pass `--instance`), bring the system up in the scenario's initial state,
drive its events, judge every expectation on the server's API and event record,
write the evidence and stop the system. When the runner fails, or a local run
gets Ctrl+C, it still stops the system and writes the result, with the verdict
cannot tell. Ctrl+C on `simple remote` ends the whole run there at once,
without a result.

| Verdict | Exit code | Meaning |
| --- | --- | --- |
| `holds` | 0 | every applicable expectation was seen to hold |
| `does-not-hold` | 10 | at least one expectation was seen not to hold |
| `cannot-tell` | 11 | something could not be observed or driven; never a pass |
| `not-applicable` | 12 | no expectation applied |

Besides the verdicts: 2, the file is not valid (nothing started); 7, another
run held the scenario instance for 5 minutes; 1, the system could not be
stopped, whatever the verdict.

The evidence directory, `<time>-<scenario>-<run id>`, is printed on the line
`Evidence:`. That line shows where the run wrote it; after `simple remote` read
the local copy under the run directory the last line prints. Here it is under
`%LOCALAPPDATA%\simple\scenario-runs\`. It holds:

- `result.json`: the run's outcome. Read three things:
  - `verdict` (and `asExpected`, against `expectedVerdict`);
  - `expectations[]`: each one's `name` (its `id` in the file, or
    `expectation <n>`), `verdict`, `reason` and `line` in the file;
    `events[]`: each action's `state` and `detail`; `driveFailure` when the
    scenario could not be driven;
  - `teardown`: whether the system was stopped. `verdict` says nothing about
    that, so a run whose `teardown.stopped` and `teardown.kept` are both false
    failed (exit code 1) even when its verdict is `holds`. With `--keep-up`,
    `kept` is true and the system is meant to be running.
- `timeline.jsonl` (what happened when), `events.jsonl` (the server's event
  record), `status.json` (the server's last status), `logs/` (every component's
  program log and console output), `scenario.yaml` (the file that ran).
  `runInstance` in `result.json` is the instance the run used
  (`<instance>-scenario`); `instance` is set only when `--keep-up` left that
  system running.

To look into a system for longer than a run, bring one up with
`./simple remote up` and use `./simple remote status`, `events` and `logs`
(next section), then `./simple remote down`. `simple remote scenario` refuses
`--keep-up`: a system kept up there would end with the SSH session. On a
machine that can carry the system itself, `./simple scenario run <name>
--keep-up` leaves the run's system up for `./simple status --instance
<instance>-scenario` and the like, until `./simple down --instance
<instance>-scenario`.

## Looking into a running system

`./simple up --terminals 2` brings up the whole local system (fake RIoT, MES
gateway with its fake MES, server, one IO simulator and one headless onboard
terminal per terminal) and returns once all are ready. Before the server
starts, its command `import-site` imports a site's starting configuration
(`sites/sample/` unless `--site` names another; a scenario's `site:`),
checked against the fake RIoT's maps; once the database has one it imports
nothing, and when the file is not valid `up` fails with the problems listed
(`sites/README.md`). `./simple down` stops it,
`--reset` also drops its database and server key, and those of
`<instance>-scenario`, the instance its scenario runs used. Run
`./simple down --reset` before you delete a clone or worktree, as you run
`./simple remote forget` for what it left on the remote machine. `./simple remote up` and the other remote
commands do the same on the remote machine, where it keeps running until
`./simple remote down`.

- `./simple status`: every process (running, ready, its URL, its program log
  directory after `log`, its console output file after `output`), then the
  server's own view: MES gateway, RIoT, each onboard terminal and its IO, and
  any terminal waiting for confirmation with its pairing code.
- Onboard terminals: each local terminal has a key of its own
  (`Simple-Terminal-<instance>-<n>`, deleted by `down --reset`), and the
  server uses its reports only once its key is confirmed (ADR 0025). `up`
  confirms every terminal it starts, as an engineer at the vehicle would; a
  scenario with `confirm-terminals: false` leaves them waiting and confirms
  one with the `confirm-terminal` action (`scenarios/terminal-pairing.yaml`).
- `./simple events --since 5m`: the server's event record, oldest first. This is
  the observation surface for what happened; the event types are in
  `src/Simple.Server/Events/EventRecord.cs`.
- `./simple logs <process> --tail 50`: the program log the process writes itself,
  credentials redacted. `./simple logs <process> --output`: what it printed to
  its console, which is where a refused start or a runtime crash shows up; read
  it when `logs` ends with 5 (no program log yet). `./simple logs build --output`
  is the last build's output.
- The server's API listens on loopback and needs a login (account `admin`, the
  password in `%LOCALAPPDATA%\simple\instances\<instance>\admin-password.txt`).
  `GET /api/v1/status` and `GET /api/v1/events` are what scenarios observe.

## Tests

Tests run on Microsoft Testing Platform (MTP), not VSTest: filter with
`--filter-class`, `--filter-method`, `--filter-trait` or `--filter-query`;
`--filter` is VSTest's and does not work here. A filter needs `--project`:
across the whole solution every other test project finds no test, ends with 8
("nothing ran"), and so does the run.

```
dotnet test --project tests/Simple.Server.Tests --filter-class "*MesGatewayTests"
./simple remote test --project tests/Simple.Cli.Tests --filter-class "*ScenarioTests"
./simple remote test
```

Exit code 8 is "cannot tell", not a pass: fix the filter rather than pass
`--ignore-exit-code 8`. Tests that need a database make their own and drop it.

### Contract tests and the real RIoT

`tests/Simple.Contracts.Tests` holds what has been observed about the real
fleet manager (RIoT) as tests, each tagged with its source:
`dotnet test --project tests/Simple.Contracts.Tests --filter-trait "Contract=BC-ORDER-004"`.
They run against a fake RIoT inside the test process, always on CI and by
default everywhere (`docs/adr/0047-riot-contract-tests-fake-by-default-real-read-only.md`).
A run against the real RIoT is chosen by a person for that one run: they write
a settings file outside every git clone, with a `runUntil` at most 12 hours
ahead, and point `SIMPLE_CONTRACTS_RIOT_SETTINGS` at it. Then only the
read-only contracts run, behind an allow-list of read requests. With `CI=true`
(which `simple remote` sets on the remote machine) the real RIoT is refused
outright. Leave that variable alone: setting it, or writing such a file, is the
person's decision, not part of a test run.

## Nothing real, by default

Every default in this repository points at fakes on loopback, and the command
line has no switch that points at anything real. In practice:

- `simple up`, `simple scenario run` and the `simple remote` commands that run
  `simple` there (`up`, `scenario`, `down` and the like) start a local system
  of fakes only, on the machine that runs them. `simple remote exec` runs
  whatever command you give it, so what it reaches is up to that command. They drop
  `Simple__*`, `ConnectionStrings__*`, `ASPNETCORE_*`, `Kestrel__*` and
  `DOTNET_ENVIRONMENT` from the components' environment, and accept only
  LocalDB or a named SQL Server instance on that machine.
- To try a component against something else, the settings come from outside the
  repository (a file outside the clone, an environment variable) and a person
  selects them for that run. Never write a real endpoint or a credential into
  a file in the repository, a scenario included.
- Anything that moves a real vehicle or sends an emergency stop is authorised by
  the product owner for every run, with someone on site. No test, scenario or
  command here does it.
