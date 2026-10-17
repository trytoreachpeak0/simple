using System.Text;
using Simple.Protocol.V1;

namespace Simple.OnboardTerminal.Core;

/// <summary>
/// What the person at the vehicle did, waiting to be reported (ADR 0024).
/// An input goes into every report until the server acknowledges a report
/// that carried it; when the stream ends, inputs not yet acknowledged are
/// dropped, never carried over to the next stream. While the terminal is not
/// connected nothing is taken in: the screen says it is disconnected and
/// scanning and buttons do nothing.
/// </summary>
public sealed class OnboardInputs
{
    private readonly Lock gate = new();
    private readonly List<Pending> pending = [];
    private readonly StringBuilder typed = new();
    private ulong lastId;
    private bool accepting;

    /// <summary>Raised when an input is taken in, so that the next report goes out at once.</summary>
    public event Action? Added;

    /// <summary>
    /// Keystrokes, as a barcode scanner types them: characters and then Enter.
    /// Each Enter ends one scan of the characters typed since the last one;
    /// characters not yet followed by Enter wait for it. Returns the scans this
    /// completed, or null when the terminal is not connected (the keystrokes
    /// are then dropped).
    /// </summary>
    public IReadOnlyList<(ulong Id, string Text)>? Type(string keystrokes)
    {
        ArgumentNullException.ThrowIfNull(keystrokes);
        var scans = new List<(ulong Id, string Text)>();
        lock (gate)
        {
            if (!accepting)
            {
                typed.Clear();
                return null;
            }

            foreach (var key in keystrokes)
            {
                if (key is not ('\r' or '\n'))
                {
                    typed.Append(key);
                }
                else if (typed.Length > 0)
                {
                    // An empty Enter (the \n of \r\n) is no scan.
                    var text = typed.ToString();
                    typed.Clear();
                    var id = Add(new OnboardInput { Scan = new Scan { Text = text } });
                    scans.Add((id, text));
                }
            }
        }

        if (scans.Count > 0)
        {
            Added?.Invoke();
        }

        return scans;
    }

    /// <summary>A button on the screen was pressed. Returns its input id, or null when the terminal is not connected.</summary>
    public ulong? Press(string button)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(button);
        ulong id;
        lock (gate)
        {
            if (!accepting)
            {
                return null;
            }

            id = Add(new OnboardInput { ButtonPress = new ButtonPress { Button = button } });
        }

        Added?.Invoke();
        return id;
    }

    /// <summary>Inputs for the report <paramref name="reportSeq"/>: every one not yet acknowledged.</summary>
    internal IReadOnlyList<OnboardInput> ForReport(ulong reportSeq)
    {
        lock (gate)
        {
            foreach (var item in pending)
            {
                item.FirstReportSeq ??= reportSeq;
            }

            return pending.Select(p => p.Input).ToList();
        }
    }

    /// <summary>The server has received every report up to <paramref name="reportSeq"/>.</summary>
    internal void Acknowledge(ulong reportSeq)
    {
        lock (gate)
        {
            pending.RemoveAll(p => p.FirstReportSeq <= reportSeq);
        }
    }

    /// <summary>A stream is up: inputs are taken in from now on.</summary>
    internal void Open()
    {
        lock (gate)
        {
            accepting = true;
        }
    }

    /// <summary>The stream has ended: drop what was not acknowledged, and take nothing in until the next one.</summary>
    internal void Close()
    {
        lock (gate)
        {
            accepting = false;
            pending.Clear();
            typed.Clear();
        }
    }

    private ulong Add(OnboardInput input)
    {
        input.Id = ++lastId;
        pending.Add(new Pending(input));
        return input.Id;
    }

    private sealed class Pending(OnboardInput input)
    {
        public OnboardInput Input { get; } = input;

        public ulong? FirstReportSeq { get; set; }
    }
}
