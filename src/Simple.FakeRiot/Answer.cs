namespace Simple.FakeRiot;

/// <summary>
/// One answer in RIoT's business envelope <c>{code, message, msgDetail, result, tid}</c>.
/// Business failures come back with HTTP 200 and a non-zero code, as on real
/// RIoT (BC-AUTH-001, Q-007). <see cref="HasResult"/> false leaves the
/// <c>result</c> member out altogether, which is how real RIoT answers a
/// lookup of an order it does not know (R10, R11).
/// </summary>
public sealed record Answer(string Code, string Message, object? Result, bool HasResult, string MsgDetail = "")
{
    /// <summary>
    /// The fake's own code for a request whose answer on real RIoT nobody has
    /// observed. Nothing may rely on it: it marks a gap in what is known, not
    /// a RIoT behaviour.
    /// </summary>
    public const string UnobservedCode = "FAKE-UNOBSERVED";

    public bool Succeeded => Code == "0";

    public static Answer Ok(object? result) => new("0", "成功", result, true);

    /// <summary>Success without a <c>result</c> member, as real RIoT answers commands and unknown orders.</summary>
    public static Answer OkWithoutResult() => new("0", "成功", null, false);

    public static Answer Fail(string code, string message, string detail = "") => new(code, message, null, false, detail);

    /// <summary>RIoT's general internal error. Real RIoT answers it with a Java exception in msgDetail; its effect is unknown (R6).</summary>
    public static Answer InternalError(string exception = "java.lang.NullPointerException") => Fail("00002", "内部错误", exception);

    public static Answer Unobserved(string what) =>
        Fail(UnobservedCode, $"假 RIoT：没有观测过真实 RIoT 在这种情况下怎么回答（{what}）。");

    public IReadOnlyDictionary<string, object?> ToWire()
    {
        var wire = new Dictionary<string, object?>
        {
            ["code"] = Code,
            ["message"] = Message,
            ["msgDetail"] = MsgDetail,
        };
        if (HasResult)
        {
            wire["result"] = Result;
        }

        wire["tid"] = "";
        return wire;
    }
}
