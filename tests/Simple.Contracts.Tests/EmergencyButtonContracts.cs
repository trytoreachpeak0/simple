namespace Simple.Contracts.Tests;

/// <summary>
/// The emergency-stop button on the vehicle, and how it meets RIoT's software
/// stop (ADR 0009, ADR 0010; #11). Someone presses the button at the vehicle,
/// so on real RIoT these skip.
/// </summary>
public sealed class EmergencyButtonContracts : OrderContractTest
{
    /// <summary>
    /// Pressing the button does not hang the executing order: it stays
    /// EXECUTING, held by the stop at speed 0, and the vehicle reads
    /// CAN_NOT_RECOVER. Round 28 watched for about 14 minutes and saw no HANG.
    /// ADR 0010 said the opposite until the product owner corrected it to the
    /// lab's result on 2026-10-18.
    /// </summary>
    [Fact]
    [Contract("round 27", "round 28", "Q-035", "BC-ORDER-015", "ADR 0010", "#11")]
    public async Task PressingTheButtonDoesNotHangTheExecutingOrder()
    {
        var control = Target.RequireControl("按下车上的急停按钮");
        var (upperId, _) = await ExecutingAsync("button-no-hang");

        await control.SetVehicleAsync(Facts.VehicleKey, emergencyButtonPressed: true);
        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "CAN_NOT_RECOVER", "急停（CAN_NOT_RECOVER）");

        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Executing, "执行中、不进挂起");
        Assert.Equal(0d, (await VehicleAsync()).Speed);
    }

    /// <summary>
    /// While the button is still pressed, releasing the stop in RIoT does not
    /// release it: it is set again at once, the way a pressed bumper does.
    /// </summary>
    [Fact]
    [Contract("ADR 0009", "#11", "round 27")]
    [NotVerifiedOnRealRiot("部分实测：第 27 轮 A 节硬件急停要物理松开后 cancelEmergency 才回 OK；'在 RIoT 里解除会立刻再触发'是产品负责人的说法（ADR 0009；#11 #14 补充）")]
    public async Task WhileTheButtonIsPressedAReleaseInRiotDoesNotReleaseTheStop()
    {
        var control = Target.RequireControl("按下车上的急停按钮");
        await control.SetVehicleAsync(Facts.VehicleKey, emergencyButtonPressed: true);
        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "CAN_NOT_RECOVER", "急停（CAN_NOT_RECOVER）");

        await Riot.Device.CancelEmergencyStopAsync(Facts.VehicleKey, Cancel);

        await ThroughoutAsync(_ => VehicleAsync(), v => v.EmergencyState != "OK", "急停不解除");
    }

    /// <summary>
    /// The button sets the software stop as well: once the button is let go,
    /// the vehicle is still stopped (CAN_RECOVER) until the stop is released in RIoT.
    /// </summary>
    [Fact]
    [Contract("ADR 0009", "#11", "round 27")]
    [NotVerifiedOnRealRiot("部分实测：第 27 轮 A 节物理松开急停后要 cancelEmergency 才回 OK；'一类触发同时触发二类、一类消除后二类还在'是产品负责人的说法（ADR 0009；#7 评论第 5 条）")]
    public async Task OnceTheButtonIsLetGoTheStopStaysUntilReleasedInRiot()
    {
        var control = Target.RequireControl("按下再松开车上的急停按钮");
        await control.SetVehicleAsync(Facts.VehicleKey, emergencyButtonPressed: true);
        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "CAN_NOT_RECOVER", "急停（CAN_NOT_RECOVER）");

        await control.SetVehicleAsync(Facts.VehicleKey, emergencyButtonPressed: false);
        await ThroughoutAsync(_ => VehicleAsync(), v => v.EmergencyState == "CAN_RECOVER", "松开按钮后仍急停（CAN_RECOVER）");
        await Riot.Device.CancelEmergencyStopAsync(Facts.VehicleKey, Cancel);

        await UntilAsync(_ => VehicleAsync(), v => v.EmergencyState == "OK", "在 RIoT 里解除后 OK");
    }
}
