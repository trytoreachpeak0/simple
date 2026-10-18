using System.CommandLine;
using Simple.Cli.Instances;

namespace Simple.Cli.Scenarios;

/// <summary>simple scenario list, validate and run.</summary>
public static class ScenarioCommand
{
    public static Command Create(CliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var list = new Command("list", $"List the scenarios under {ScenarioFile.Directory}/ with their titles, expected verdicts and whether each is valid. Exit code 0.");
        list.SetAction(parse => Write(parse, environment, List(environment.Repository)));

        var names = new Argument<string[]>("scenarios")
        {
            Description = $"Scenario names under {ScenarioFile.Directory}/ (infrastructure-up) or paths to .yaml files. Default: every scenario.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var validate = new Command(
            "validate",
            $"Check scenario files against the format ({ScenarioFile.Directory}/scenario.schema.json), the rules it cannot express and their site, naming the line of every problem. Exit codes 0, 1, 2 (a scenario is not valid).")
        {
            names,
        };
        validate.SetAction(parse => Write(parse, environment, Validate(environment.Repository, parse.GetValue(names) ?? [])));

        var name = new Argument<string>("scenario")
        {
            Description = $"A scenario name under {ScenarioFile.Directory}/ (infrastructure-up) or a path to a .yaml file.",
        };
        var evidence = new Option<string?>("--evidence")
        {
            Description = $"The directory the run's evidence directory is made in. Default: scenario-runs under {InstanceStore.HomeVariable} (%LOCALAPPDATA%\\simple), outside the clone.",
        };
        var keepUp = new Option<bool>("--keep-up")
        {
            Description = "Leave the system running after the run, to look into it with simple status, events and logs --instance <instance>. simple down stops it.",
        };
        var keyStore = new Option<string>(SimpleCommand.ServerKeyStoreOption)
        {
            Description = "Internal, as for up: where the server keeps its key, User (default) or Machine.",
            Hidden = true,
            DefaultValueFactory = _ => nameof(ServerKeyStore.User),
        };
        keyStore.Validators.Add(result =>
        {
            if (!ServerKey.TryParse(result.GetValueOrDefault<string>(), out _))
            {
                result.AddError($"{SimpleCommand.ServerKeyStoreOption} '{result.GetValueOrDefault<string>()}' is neither User nor Machine.");
            }
        });
        var run = new Command(
            "run",
            "Run one scenario as its own local system: reset the instance (<instance>-scenario unless --instance is given), bring it up in the scenario's initial state, "
            + "drive its events through the fakes, judge its expectations on the server's API and event record, write the evidence and stop the system. "
            + "On Ctrl+C or any failure it still stops the system and writes the result (cannot tell). "
            + "Ends with the verdict: 0 holds, 10 does not hold, 11 cannot tell, 12 not applicable; 1 when the system could not be stopped; "
            + "2 when the file is not valid; 7 when another run holds the instance.")
        {
            name,
            evidence,
            keepUp,
            keyStore,
        };
        run.SetAction(async (parse, cancellationToken) =>
        {
            var json = parse.GetValue<bool>(SimpleCommand.JsonOption);
            Outcome outcome;
            if (SqlServerTarget.Refusal(environment.SqlServer) is { } refusal)
            {
                outcome = Outcome.Fail(ExitCodes.Usage, refusal);
            }
            else if (SimpleCommand.InstanceRefusal(parse, environment.Repository, starts: false) is { } refused)
            {
                // --instance names the run's own instance; only the default derives it from the clone's.
                outcome = Outcome.Fail(ExitCodes.Usage, refused);
            }
            else if (ScenarioFile.Find(environment.Repository, parse.GetValue(name)!) is not { } found)
            {
                outcome = Outcome.Fail(ExitCodes.Usage, $"No scenario '{parse.GetValue(name)}': neither a .yaml file nor a name under {Path.Combine(environment.Repository, ScenarioFile.Directory)}. simple scenario list shows them.");
            }
            else
            {
                var (scenario, problems) = ScenarioFile.Load(environment.Repository, found.Name, found.Path);
                if (scenario is null)
                {
                    outcome = Invalid([(found.Name, found.Path, problems)]);
                }
                else
                {
                    try
                    {
                        var store = new InstanceStore(environment.Home);
                        var instance = parse.GetValue<string?>(SimpleCommand.InstanceOption)
                            ?? ScenarioRunner.InstanceFor(SimpleCommand.InstanceOf(parse, environment.Repository));
                        var keys = ServerKey.TryParse(parse.GetValue(keyStore), out var chosen) ? chosen : ServerKeyStore.User;
                        outcome = await new ScenarioRunner(environment.Repository, store, environment.Error).RunAsync(
                            scenario,
                            instance,
                            environment.SqlServer,
                            keys,
                            parse.GetValue(evidence) is { Length: > 0 } root ? Path.GetFullPath(root) : ScenarioRunner.DefaultEvidenceRoot(store),
                            parse.GetValue(keepUp),
                            cancellationToken);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        outcome = Outcome.Fail(ExitCodes.Unexpected, e.ToString());
                    }
                }
            }

            return outcome.Write(json, environment.Out, environment.Error);
        });

        return new Command(
            "scenario",
            "Scenarios: one file per scenario under scenarios/, shared by CI, people and AI sessions (scenarios/README.md). " + SimpleCommand.ExitCodesOf("scenario"))
        {
            list,
            validate,
            run,
        };
    }

    /// <summary>Whether the parsed command is simple scenario run.</summary>
    public static bool IsRun(ParseResult parse)
    {
        ArgumentNullException.ThrowIfNull(parse);
        return parse.CommandResult is { Command.Name: "run", Parent: System.CommandLine.Parsing.CommandResult { Command.Name: "scenario" } };
    }

    private static int Write(ParseResult parse, CliEnvironment environment, Outcome outcome) =>
        outcome.Write(parse.GetValue<bool>(SimpleCommand.JsonOption), environment.Out, environment.Error);

    private static Outcome List(string repository)
    {
        var items = new List<object>();
        var lines = new List<string>();
        foreach (var (name, path) in ScenarioFile.All(repository))
        {
            var (scenario, problems) = ScenarioFile.Load(repository, name, path);
            items.Add(new
            {
                Name = name,
                Path = path,
                scenario?.Title,
                scenario?.Site,
                ExpectedVerdict = scenario?.ExpectedVerdict.Name(),
                Valid = scenario is not null,
                Problems = problems.Select(p => p.ToString()),
            });
            lines.Add(scenario is null
                ? $"{name,-36} NOT VALID ({problems.Count} problems: simple scenario validate {name})"
                : $"{name,-36} {scenario.Site,-8} expects {scenario.ExpectedVerdict.Name(),-15} {scenario.Title}");
        }

        return Outcome.Ok(new { Scenarios = items }, lines.Count > 0 ? lines : [$"No scenarios under {Path.Combine(repository, ScenarioFile.Directory)}."]);
    }

    private static Outcome Validate(string repository, string[] references)
    {
        var targets = new List<(string Name, string Path)>();
        if (references.Length == 0)
        {
            targets.AddRange(ScenarioFile.All(repository));
        }
        else
        {
            foreach (var reference in references)
            {
                if (ScenarioFile.Find(repository, reference) is not { } found)
                {
                    return Outcome.Fail(ExitCodes.Usage, $"No scenario '{reference}': neither a .yaml file nor a name under {Path.Combine(repository, ScenarioFile.Directory)}.");
                }

                targets.Add(found);
            }
        }

        var invalid = new List<(string Name, string Path, IReadOnlyList<ScenarioProblem> Problems)>();
        foreach (var (name, path) in targets)
        {
            var (scenario, problems) = ScenarioFile.Load(repository, name, path);
            if (scenario is null)
            {
                invalid.Add((name, path, problems));
            }
        }

        return invalid.Count > 0
            ? Invalid(invalid)
            : Outcome.Ok(new { Valid = targets.Select(t => t.Name) }, [.. targets.Select(t => $"{t.Name}: valid")]);
    }

    private static Outcome Invalid(List<(string Name, string Path, IReadOnlyList<ScenarioProblem> Problems)> invalid)
    {
        var lines = invalid.SelectMany(i => i.Problems.Select(p => $"{i.Path}: {p}")).ToList();
        return Outcome.Fail(
            ExitCodes.Usage,
            $"{invalid.Count} scenario file(s) not valid:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}",
            new { Invalid = invalid.Select(i => new { i.Name, i.Path, Problems = i.Problems }) });
    }
}
