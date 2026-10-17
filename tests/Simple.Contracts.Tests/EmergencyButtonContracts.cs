namespace Simple.Contracts.Tests;

/// <summary>
/// The emergency-stop button on the vehicle, and how it meets RIoT's software
/// stop. All of it is the product owner's statements (ADR 0009, ADR 0010;
/// #11). Someone presses the button at the vehicle, so on real RIoT these skip.
/// </summary>
public sealed class EmergencyButtonContracts : OrderContractTest
{
    /// <summary>Pressing the button hangs the executing order (a software stop does not, BC-ORDER-015).</summary>
    [Fact]
    [Contract("ADR 0010", "#11")]
    [NotVerifiedOnRealRiot("按车上的急停按钮会让执行中的订单进挂起（产品负责人，ADR 0010；#7 评论第 5 条；和时间的关系不知道，假 RIoT 当即挂起）")]
    public async Task PressingTheButtonHangsTheExecutingOrder()
    {
        var control = Target.RequireControl("按下车上的急停按钮");
        var (upperId, _) = await ExecutingAsync("button-hang");

        await control.SetVehicleAsync(Facts.VehicleKey, emergencyButtonPressed: true);

        await UntilStateAsync(upperId, OrderStates.Hang);
        Assert.Equal("CAN_NOT_RECOVER", (await VehicleAsync()).EmergencyState);
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
