namespace Simple.IoSimulator.Core;

/// <summary>
/// How long the cabinet's mechanics take. The defaults are the previous
/// simulator's, which the field confirmed for the C2000 module's pulse mode.
/// </summary>
/// <param name="PulseWidth">How long an unlock output stays on before the module turns it off by itself (pulse mode, ADR 0024).</param>
/// <param name="UnlockFeedbackDelay">From the unlock output going on to the lock feedback reading open.</param>
/// <param name="LockFeedbackDelay">From the door being closed to the lock feedback reading locked.</param>
/// <param name="LightCurtainDelay">From cargo being put in or taken out to the light curtain reading it.</param>
public sealed record CabinetTiming(TimeSpan PulseWidth, TimeSpan UnlockFeedbackDelay, TimeSpan LockFeedbackDelay, TimeSpan LightCurtainDelay)
{
    public static CabinetTiming Default { get; } = new(
        TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50));
}

/// <summary>
/// The vehicle's cabinet of <see cref="SlotCount"/> slots as its IO module
/// sees it. Slot n's unlock is DO n, its lock feedback DI n (1 = locked) and
/// its light curtain DI n + 8 (0 = cargo in the way), as wired on the
/// vehicles (the previous simulator's settings, confirmed in the field).
/// </summary>
/// <remarks>
/// The facts (door open, cargo in) change at once; what the sensors read
/// follows after the delays in <see cref="CabinetTiming"/>. A pinned sensor
/// reads its pinned value whatever the facts are, which is how a test makes
/// the readings contradict what happened. <see cref="Changed"/> is raised,
/// outside the cabinet's lock, after anything an IO point or a slot shows
/// has changed.
/// </remarks>
public sealed class Cabinet : IDisposable
{
    public const int SlotCount = 8;

    /// <summary>The IO module's outputs and inputs: 16 of each.</summary>
    public const int ChannelCount = 16;

    private readonly TimeProvider time;
    private readonly CabinetTiming timing;
    private readonly Lock gate = new();
    private readonly bool[] outputs = new bool[ChannelCount];
    private readonly Scheduled[] pulses = Enumerable.Range(0, ChannelCount).Select(_ => new Scheduled()).ToArray();
    private readonly Slot[] slots = Enumerable.Range(1, SlotCount).Select(n => new Slot(n)).ToArray();
    private bool disposed;

    public Cabinet(TimeProvider time, CabinetTiming timing)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(timing);
        this.time = time;
        this.timing = timing;
    }

    /// <summary>Raised after an IO point or a slot's state has changed; never under the cabinet's lock.</summary>
    public event Action? Changed;

    /// <summary>Raised with the slot number when an unlock popped a closed door open; never under the cabinet's lock.</summary>
    public event Action<int>? DoorOpened;

    /// <summary>DO1-DO16 as the module holds them.</summary>
    public IReadOnlyList<bool> Outputs()
    {
        lock (gate)
        {
            return [.. outputs];
        }
    }

    /// <summary>DI1-DI16 as the module reads them.</summary>
    public IReadOnlyList<bool> Inputs()
    {
        lock (gate)
        {
            return ReadInputs();
        }
    }

    public CabinetState State()
    {
        lock (gate)
        {
            return new CabinetState(
                [.. outputs],
                ReadInputs(),
                slots.Select(s => new SlotState(s.Number, s.DoorOpen, s.Cargo, s.LockFeedbackPin, s.LightCurtainPin)).ToList());
        }
    }

    /// <summary>
    /// The module was told to set DO <paramref name="number"/> (1-16). On a
    /// slot's unlock output, the output pulses: it goes off by itself after
    /// <see cref="CabinetTiming.PulseWidth"/>, the door pops open and the lock
    /// feedback reads open after <see cref="CabinetTiming.UnlockFeedbackDelay"/>.
    /// </summary>
    public void WriteOutput(int number, bool value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, ChannelCount);
        var opened = false;
        lock (gate)
        {
            outputs[number - 1] = value;
            if (number <= SlotCount && value)
            {
                var slot = slots[number - 1];
                opened = !slot.DoorOpen;
                slot.DoorOpen = true;
                Schedule(slot.LockFeedback, timing.UnlockFeedbackDelay, () => slot.LockSensed = false);
                Schedule(pulses[number - 1], timing.PulseWidth, () => outputs[number - 1] = false);
            }
        }

        Changed?.Invoke();
        if (opened)
        {
            DoorOpened?.Invoke(number);
        }
    }

    /// <summary>The person puts cargo into slot <paramref name="number"/> (1-8) through its open door.</summary>
    public SlotResult Load(int number) => AtTheSlot(number, slot =>
        !slot.DoorOpen ? SlotResult.DoorNotOpen
        : slot.Cargo ? SlotResult.SlotNotEmpty
        : SetCargo(slot, true));

    /// <summary>The person takes the cargo out of slot <paramref name="number"/> (1-8) through its open door.</summary>
    public SlotResult Unload(int number) => AtTheSlot(number, slot =>
        !slot.DoorOpen ? SlotResult.DoorNotOpen
        : !slot.Cargo ? SlotResult.SlotEmpty
        : SetCargo(slot, false));

    /// <summary>The person closes the door of slot <paramref name="number"/> (1-8); its lock feedback reads locked after <see cref="CabinetTiming.LockFeedbackDelay"/>.</summary>
    public SlotResult Close(int number) => AtTheSlot(number, slot =>
    {
        if (!slot.DoorOpen)
        {
            return SlotResult.DoorNotOpen;
        }

        slot.DoorOpen = false;
        Schedule(slot.LockFeedback, timing.LockFeedbackDelay, () => slot.LockSensed = true);
        return SlotResult.Done;
    });

    /// <summary>Pins the raw value slot <paramref name="number"/>'s lock feedback reads (1 = locked); null reads the facts again.</summary>
    public void PinLockFeedback(int number, bool? raw) => AtTheSlot(number, slot =>
    {
        slot.LockFeedbackPin = raw;
        return SlotResult.Done;
    });

    /// <summary>Pins the raw value slot <paramref name="number"/>'s light curtain reads (0 = cargo in the way); null reads the facts again.</summary>
    public void PinLightCurtain(int number, bool? raw) => AtTheSlot(number, slot =>
    {
        slot.LightCurtainPin = raw;
        return SlotResult.Done;
    });

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var scheduled in pulses.Concat(slots.SelectMany(s => new[] { s.LockFeedback, s.LightCurtain })))
            {
                scheduled.Timer?.Dispose();
            }
        }
    }

    /// <summary>Runs <paramref name="act"/> on the slot under the lock, and tells of the change when it was done.</summary>
    private SlotResult AtTheSlot(int number, Func<Slot, SlotResult> act)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, SlotCount);
        SlotResult result;
        lock (gate)
        {
            result = act(slots[number - 1]);
        }

        if (result == SlotResult.Done)
        {
            Changed?.Invoke();
        }

        return result;
    }

    /// <summary>Called under the lock.</summary>
    private SlotResult SetCargo(Slot slot, bool cargo)
    {
        slot.Cargo = cargo;
        Schedule(slot.LightCurtain, timing.LightCurtainDelay, () => slot.CargoSensed = cargo);
        return SlotResult.Done;
    }

    /// <summary>Called under the lock.</summary>
    private bool[] ReadInputs()
    {
        var inputs = new bool[ChannelCount];
        foreach (var slot in slots)
        {
            inputs[slot.Number - 1] = slot.LockFeedbackPin ?? slot.LockSensed;
            inputs[slot.Number - 1 + SlotCount] = slot.LightCurtainPin ?? !slot.CargoSensed;
        }

        return inputs;
    }

    /// <summary>
    /// Runs <paramref name="change"/> under the lock after <paramref name="delay"/>,
    /// in place of whatever <paramref name="scheduled"/> held. A replaced
    /// change whose timer had already fired and is waiting for the lock finds
    /// its generation gone and does nothing: the lock feedback of a door
    /// closed just before an unlock never reads locked after the unlock.
    /// Called under the lock.
    /// </summary>
    private void Schedule(Scheduled scheduled, TimeSpan delay, Action change)
    {
        scheduled.Timer?.Dispose();
        var generation = ++scheduled.Generation;
        scheduled.Timer = time.CreateTimer(
            _ =>
            {
                lock (gate)
                {
                    if (disposed || scheduled.Generation != generation)
                    {
                        return;
                    }

                    change();
                }

                Changed?.Invoke();
            },
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>One pending change: its timer, and which scheduling it is.</summary>
    private sealed class Scheduled
    {
        public ITimer? Timer { get; set; }

        public int Generation { get; set; }
    }

    private sealed class Slot(int number)
    {
        public Scheduled LockFeedback { get; } = new();

        public Scheduled LightCurtain { get; } = new();

        public int Number { get; } = number;

        public bool DoorOpen { get; set; }

        public bool Cargo { get; set; }

        /// <summary>What the lock feedback senses: true for locked.</summary>
        public bool LockSensed { get; set; } = true;

        /// <summary>What the light curtain senses: true for cargo in the way.</summary>
        public bool CargoSensed { get; set; }

        public bool? LockFeedbackPin { get; set; }

        public bool? LightCurtainPin { get; set; }
    }
}

/// <summary>What became of something the person at the slot tried.</summary>
public enum SlotResult
{
    Done,

    /// <summary>The door is closed: cargo cannot go in or out, and it cannot be closed again.</summary>
    DoorNotOpen,

    /// <summary>There is cargo in the slot already.</summary>
    SlotNotEmpty,

    /// <summary>There is no cargo to take out.</summary>
    SlotEmpty,
}

/// <param name="Outputs">DO1-DO16.</param>
/// <param name="Inputs">DI1-DI16 as read, pins included.</param>
/// <param name="Slots">Slot 1 first.</param>
public sealed record CabinetState(IReadOnlyList<bool> Outputs, IReadOnlyList<bool> Inputs, IReadOnlyList<SlotState> Slots);

/// <param name="Number">1-8.</param>
/// <param name="DoorOpen">Whether the door is open (a fact, whatever the lock feedback reads).</param>
/// <param name="Cargo">Whether there is cargo in the slot (a fact, whatever the light curtain reads).</param>
/// <param name="LockFeedbackPin">The raw value the lock feedback is pinned to, or null.</param>
/// <param name="LightCurtainPin">The raw value the light curtain is pinned to, or null.</param>
public sealed record SlotState(int Number, bool DoorOpen, bool Cargo, bool? LockFeedbackPin, bool? LightCurtainPin);
