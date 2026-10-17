using Simple.IoSimulator.Core;

namespace Simple.IoSimulator.Headless;

/// <summary>
/// The control API: what tests, scenarios and AI sessions do in place of the
/// person at the slots and of a failing connection. There is no way to open
/// a door or unlock a slot here: a door opens only when the onboard terminal
/// writes the slot's unlock output over Modbus, so nothing goes around the
/// terminal. Raw values are 0 and 1, as the module reads them; inputs and
/// outputs are listed from channel 1 on.
/// </summary>
public static class ControlEndpoints
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(10);

    public static void MapControlEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api/v1").WithTags("IO simulator control");

        api.MapGet("/state", (SimulatedIoModule module) => Results.Ok(State(module)))
            .WithSummary("The module's raw outputs and inputs, every slot's door, cargo and pinned sensors, the Modbus fault and the auto-responder.");

        api.MapPost("/slots/{number:int}/load", (SimulatedIoModule module, int number) => AtTheSlot(module, number, module.Cabinet.Load))
            .WithSummary("Puts cargo into the slot through its open door; 409 door-not-open or slot-not-empty.");
        api.MapPost("/slots/{number:int}/unload", (SimulatedIoModule module, int number) => AtTheSlot(module, number, module.Cabinet.Unload))
            .WithSummary("Takes the cargo out of the slot through its open door; 409 door-not-open or slot-empty.");
        api.MapPost("/slots/{number:int}/close", (SimulatedIoModule module, int number) => AtTheSlot(module, number, module.Cabinet.Close))
            .WithSummary("Closes the slot's door; 409 door-not-open when it is closed already.");

        api.MapPut("/slots/{number:int}/lock-feedback", (SimulatedIoModule module, int number, PinRequest request) =>
                Pin(module, number, request, module.Cabinet.PinLockFeedback))
            .WithSummary("Pins the raw value the slot's lock feedback reads (1 = locked), whatever the door does; null unpins it.");
        api.MapPut("/slots/{number:int}/light-curtain", (SimulatedIoModule module, int number, PinRequest request) =>
                Pin(module, number, request, module.Cabinet.PinLightCurtain))
            .WithSummary("Pins the raw value the slot's light curtain reads (0 = cargo in the way), whatever is in the slot; null unpins it.");

        api.MapPut("/modbus-fault", (SimulatedIoModule module, ModbusFaultRequest request) =>
            {
                if (request.Mode is not { } mode || request.DelayMs is < 0)
                {
                    return Results.BadRequest(new { Error = "mode is one of none, no-response, disconnect, delay; delayMs is not negative" });
                }

                module.Server.SetFault(mode, TimeSpan.FromMilliseconds(request.DelayMs ?? 0));
                return Results.Ok(Fault(module));
            })
            .WithSummary("Switches a communication fault on (no-response, disconnect, delay with delayMs) or off (none).");

        api.MapPost("/auto-responder", (SimulatedIoModule module, AutoResponderRequest request) =>
            {
                if (request.Action is not { } action || request.Doors is not (> 0 and <= Cabinet.SlotCount * 100)
                    || request.ActSeconds is < 0 || request.CloseSeconds is < 0 || request.WithinSeconds is <= 0)
                {
                    return Results.BadRequest(new { Error = "action is load or unload, doors is above 0, the seconds are not negative and withinSeconds is above 0" });
                }

                return Results.Ok(AutoResponderView.Of(module.AutoResponder.Start(new AutoResponse(
                    action,
                    request.Doors.Value,
                    TimeSpan.FromSeconds(request.ActSeconds ?? 4),
                    TimeSpan.FromSeconds(request.CloseSeconds ?? 4),
                    request.WithinSeconds is { } within ? TimeSpan.FromSeconds(within) : null))));
            })
            .WithSummary("For the next doors that open: after actSeconds (default 4) put cargo in or take it out, after closeSeconds (default 4) close the door. Replaces what it was doing.");
        api.MapGet("/auto-responder", (SimulatedIoModule module) => Results.Ok(AutoResponderView.Of(module.AutoResponder.Status())))
            .WithSummary("How far the auto-responder has got, and why it failed.");
        api.MapDelete("/auto-responder", (SimulatedIoModule module) => Results.Ok(AutoResponderView.Of(module.AutoResponder.Cancel())))
            .WithSummary("Stops the auto-responder.");
        api.MapPost("/auto-responder/wait", async (SimulatedIoModule module, double? timeoutSeconds, CancellationToken cancellationToken) =>
            {
                var timeout = timeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultWait;
                if (timeout <= TimeSpan.Zero || timeout > LongestWait)
                {
                    return Results.BadRequest(new { Error = $"timeoutSeconds is above 0 and at most {LongestWait.TotalSeconds}" });
                }

                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waiting.CancelAfter(timeout);
                AutoResponderStatus status;
                try
                {
                    status = await module.AutoResponder.WaitAsync(waiting.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    status = module.AutoResponder.Status();
                }

                var view = AutoResponderView.Of(status);
                return status.State == AutoResponderState.Done ? Results.Ok(view) : Results.Conflict(view);
            })
            .WithSummary("Waits until the auto-responder is done (200), or answers 409 with its state when it failed, was stopped, is idle or is still running after timeoutSeconds (default 60).");
    }

    private static object State(SimulatedIoModule module)
    {
        var state = module.Cabinet.State();
        return new
        {
            Modbus = module.Server.Endpoint.ToString(),
            Outputs = state.Outputs.Select(Raw),
            Inputs = state.Inputs.Select(Raw),
            Slots = state.Slots.Select(s => new
            {
                s.Number,
                s.DoorOpen,
                s.Cargo,
                LockFeedbackPin = s.LockFeedbackPin is { } locked ? Raw(locked) : (int?)null,
                LightCurtainPin = s.LightCurtainPin is { } curtain ? Raw(curtain) : (int?)null,
            }),
            ModbusFault = Fault(module),
            AutoResponder = AutoResponderView.Of(module.AutoResponder.Status()),
        };
    }

    private static object Fault(SimulatedIoModule module)
    {
        var (mode, delay) = module.Server.Fault;
        return new { Mode = mode, DelayMs = (int)delay.TotalMilliseconds };
    }

    private static int Raw(bool value) => value ? 1 : 0;

    private static IResult AtTheSlot(SimulatedIoModule module, int number, Func<int, SlotResult> act)
    {
        if (number is < 1 or > Cabinet.SlotCount)
        {
            return NoSuchSlot(number);
        }

        var result = act(number);
        return result == SlotResult.Done
            ? Results.Ok(State(module))
            : Results.Conflict(new { Error = result });
    }

    private static IResult Pin(SimulatedIoModule module, int number, PinRequest request, Action<int, bool?> pin)
    {
        if (number is < 1 or > Cabinet.SlotCount)
        {
            return NoSuchSlot(number);
        }

        if (request.Value is not (null or 0 or 1))
        {
            return Results.BadRequest(new { Error = "value is 0, 1 or null" });
        }

        pin(number, request.Value is { } raw ? raw == 1 : null);
        return Results.Ok(State(module));
    }

    private static IResult NoSuchSlot(int number) => Results.NotFound(new { Error = $"there is no slot {number}; slots are 1 to {Cabinet.SlotCount}" });

    public sealed record PinRequest(int? Value);

    public sealed record ModbusFaultRequest(ModbusFault? Mode, int? DelayMs);

    public sealed record AutoResponderRequest(SlotAction? Action, int? Doors, double? ActSeconds, double? CloseSeconds, double? WithinSeconds);

    /// <param name="Slots">The slots whose doors it took on, in the order they opened.</param>
    private sealed record AutoResponderView(
        AutoResponderState State,
        SlotAction? Action,
        int? Doors,
        double? ActSeconds,
        double? CloseSeconds,
        double? WithinSeconds,
        int DoorsHandled,
        IReadOnlyList<int> Slots,
        string? Failure)
    {
        public static AutoResponderView Of(AutoResponderStatus status) => new(
            status.State,
            status.Response?.Action,
            status.Response?.Doors,
            status.Response?.ActAfter.TotalSeconds,
            status.Response?.CloseAfter.TotalSeconds,
            status.Response?.Within?.TotalSeconds,
            status.DoorsHandled,
            status.Slots,
            status.Failure);
    }
}
