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
    [Contract("ADR 0009", "#11")]
    [NotVerifiedOnRealRiot("车上的急停按钮还按着时，在 RIoT 里解除急停会立刻再触发，软件解不开一类急停（产品负责人，ADR 0009；#11 #14 补充）")]
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
    [Contract("ADR 0009", "#11")]
    [NotVerifiedOnRealRiot("一类急停触发时同时触发二类，一类消除后二类还在，要在 RIoT 里解除（产品负责人，ADR 0009；#7 评论第 5 条）")]
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
