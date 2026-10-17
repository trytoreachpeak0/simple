namespace Simple.Contracts.Tests;

/// <summary>
/// What RIoT shows and does while a person uses a vehicle by hand: a released
/// brake, manual mode, the manual charger, driving it from Matrix. ADR 0022
/// recognises manual use from these readings; most of them are the product
/// owner's statements or few samples (#11). A person does these things at the
/// vehicle, so on real RIoT every contract here skips.
/// </summary>
public sealed class ManualUseContracts : OrderContractTest
{
    /// <summary>
    /// The brake released: the mode stays MODE_AUTO and breakSwitchState reads
    /// UNMOVABLE. The name is the wrong way round: an applied brake reads MOVABLE.
    /// </summary>
    [Fact]
    [Contract("round 21", "round 27-F", "#11", "ADR 0022")]
    public async Task AReleasedBrakeReadsUnmovableAndTheModeStaysAuto()
    {
        var control = Target.RequireControl("松开车的抱闸");
        var applied = await VehicleAsync();

        await control.SetVehicleAsync(Facts.VehicleKey, brakeReleased: true);
        var released = await VehicleAsync();

        Assert.Equal("MOVABLE", applied.BreakSwitchState);
        Assert.Equal("UNMOVABLE", released.BreakSwitchState);
        Assert.Equal("MODE_AUTO", released.Mode);
    }

    /// <summary>Releasing the brake under an executing order puts the order into HANG (9).</summary>
    [Fact]
    [Contract("BC-ORDER-015", "R13")]
    public async Task ReleasingTheBrakeHangsTheExecutingOrder()
    {
        var control = Target.RequireControl("松开车的抱闸");
        var (upperId, _) = await ExecutingAsync("brake-hang");

        await control.SetVehicleAsync(Facts.VehicleKey, brakeReleased: true);

        await UntilStateAsync(upperId, OrderStates.Hang);
    }

    /// <summary>An order made while the brake is released stays queued.</summary>
    [Fact]
    [Contract("round 29-F", "round 30", "#11")]
    public async Task AnOrderMadeWhileTheBrakeIsReleasedStaysQueued()
    {
        var control = Target.RequireControl("松开车的抱闸");
        await control.SetVehicleAsync(Facts.VehicleKey, brakeReleased: true);
        var upperId = NewUpperId("brake-queued");

        await CreateMoveAsync(upperId, Facts.ReachableStation);

        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Queueing, "排队");
    }

    /// <summary>In MODE_MANUAL a person drives the vehicle with the handset: it moves, and RIoT has no order for it.</summary>
    [Fact]
    [Contract("ADR 0022", "#11")]
    [NotVerifiedOnRealRiot("MODE_MANUAL 时人可以直接控制车前后左右行驶，车在动、没有订单（产品负责人，ADR 0022；实验室 3 个样本没记下操作方式）")]
    public async Task InManualModeAPersonDrivesTheVehicleWithoutAnOrder()
    {
        var control = Target.RequireControl("把车切到手动模式，用手控器开");
        await control.SetVehicleAsync(Facts.VehicleKey, mode: "MODE_MANUAL");

        await control.DriveAsync(Facts.VehicleKey, Facts.ReachableStation);
        var moving = await UntilAsync(_ => VehicleAsync(), v => v.Speed > 0, "车在动");
        var card = await CardAsync();

        Assert.Equal("MODE_MANUAL", moving.Mode);
        Assert.Equal("IDLE", moving.ProcState);
        Assert.Null(card.OrderTaskId);
        await UntilAsync(_ => CardAsync(), c => c.CurrentPosition == Facts.ReachableStation, "车开到站");
    }

    /// <summary>Driven from Matrix, the vehicle moves with no order while its mode still reads MODE_AUTO.</summary>
    [Fact]
    [Contract("ADR 0022", "#11")]
    [NotVerifiedOnRealRiot("在 Matrix 上让车走时 mode 仍是 MODE_AUTO，车在动、车上没有订单（推测；2026-10-11 现场一个样本，没记下操作方式；ADR 0022）")]
    public async Task DrivenFromMatrixTheVehicleMovesWithoutAnOrderInAutoMode()
    {
        var control = Target.RequireControl("在车上的 Matrix 里让车走");

        await control.DriveAsync(Facts.VehicleKey, Facts.ReachableStation);
        var moving = await UntilAsync(_ => VehicleAsync(), v => v.Speed > 0, "车在动");

        Assert.Equal("MODE_AUTO", moving.Mode);
        Assert.Equal("IDLE", moving.ProcState);
        Assert.Null((await CardAsync()).OrderTaskId);
    }

    /// <summary>
    /// With the manual charger plugged in, batteryState reads CHARGING (seen in
    /// round 42), and the vehicle cannot move: an order for it does not take it
    /// anywhere.
    /// </summary>
    [Fact]
    [Contract("round 42", "ADR 0022", "#11")]
    [NotVerifiedOnRealRiot("插着手动充电器时车动不了（产品负责人，ADR 0022）；batteryState 读 CHARGING 在第 42 轮见过")]
    public async Task WithTheManualChargerPluggedInTheVehicleReadsChargingAndDoesNotMove()
    {
        var control = Target.RequireControl("给车插上手动充电器");
        await control.SetVehicleAsync(Facts.VehicleKey, manualCharger: true);

        await CreateMoveAsync(NewUpperId("manual-charger"), Facts.ReachableStation);

        Assert.Equal("CHARGING", (await CardAsync()).BatteryState);
        await ThroughoutAsync(_ => CardAsync(), c => c.CurrentPosition == Facts.VehicleStation && c.Speed == 0, "停在原地不动");
    }

    /// <summary>At 10 % or less RIoT gives the vehicle no order: it is made and stays queued.</summary>
    [Fact]
    [Contract("BC-VEH-007", "ADR 0020")]
    [NotVerifiedOnRealRiot("电量小于等于 10% 时 RIoT 不给车派单（产品负责人 2026-10-17，BC-VEH-007；2026-10-17 agv03 现场读数与之吻合，但同时还有急停、丢定位）")]
    public async Task AtTenPercentOrLessTheVehicleIsGivenNoOrder()
    {
        var control = Target.RequireControl("让车的电量降到 10%");
        await control.SetVehicleAsync(Facts.VehicleKey, battery: 10);
        var upperId = NewUpperId("low-battery");

        await CreateMoveAsync(upperId, Facts.ReachableStation);

        Assert.Equal(10, (await CardAsync()).BatteryPercent);
        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Queueing, "排队");
    }

    /// <summary>A queued order can still be cancelled while the vehicle is in manual use (ADR 0022 cancels them).</summary>
    [Theory]
    [InlineData(ManualUse.BrakeReleased)]
    [InlineData(ManualUse.ManualMode)]
    [InlineData(ManualUse.ManualCharger)]
    [Contract("ADR 0022", "#11")]
    [NotVerifiedOnRealRiot("抱闸松着、MODE_MANUAL、插着手动充电器时，对排队、暂停、挂起的订单发取消能生效（待实测，#11 PR #13 审查补充第 1 条；ADR 0022）")]
    public async Task AQueuedOrderCanBeCancelledDuringManualUse(ManualUse use)
    {
        var control = Target.RequireControl("人手动用车（松抱闸、切手动、插手动充电器）");
        await StartAsync(control, use);
        var upperId = NewUpperId("manual-cancel");
        var created = await CreateMoveAsync(upperId, Facts.ReachableStation);

        await Riot.Tasks.CancelOrderAsync(created.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Cancelled);
    }

    /// <summary>A hung order can be cancelled while the brake is still released.</summary>
    [Fact]
    [Contract("ADR 0022", "#11")]
    [NotVerifiedOnRealRiot("抱闸松着时对挂起的订单发取消能生效（待实测，#11 PR #13 审查补充第 1 条；ADR 0022）")]
    public async Task AHungOrderCanBeCancelledWhileTheBrakeIsReleased()
    {
        var control = Target.RequireControl("松开车的抱闸");
        var (upperId, order) = await ExecutingAsync("brake-cancel-hang");
        await control.SetVehicleAsync(Facts.VehicleKey, brakeReleased: true);
        await UntilStateAsync(upperId, OrderStates.Hang);

        await Riot.Tasks.CancelOrderAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Cancelled);
    }

    /// <summary>Once the manual use ends, a queued order starts by itself.</summary>
    [Theory]
    [InlineData(ManualUse.BrakeReleased)]
    [InlineData(ManualUse.ManualMode)]
    [InlineData(ManualUse.ManualCharger)]
    [Contract("ADR 0022", "#11")]
    [NotVerifiedOnRealRiot("切回自动模式、合上抱闸、拔下手动充电器以后，排队的单自己开始执行（待实测，#11 PR #13 审查补充第 2 条；实验室第 29-F、30 轮只见过松着抱闸时一直排队）")]
    public async Task AQueuedOrderStartsByItselfOnceManualUseEnds(ManualUse use)
    {
        var control = Target.RequireControl("人手动用车，用完恢复");
        await StartAsync(control, use);
        var upperId = NewUpperId("manual-ends");
        await CreateMoveAsync(upperId, Facts.ReachableStation);
        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Queueing, "手动用车期间排队");

        await EndAsync(control, use);

        await UntilStateAsync(upperId, OrderStates.Success);
    }

    /// <summary>A hung order does not go on by itself when the brake is applied again: it needs CONTINUE_FROM_HANG.</summary>
    [Fact]
    [Contract("ADR 0022", "#11")]
    [NotVerifiedOnRealRiot("合上抱闸以后，挂起的单不会自己继续，要发从挂起继续（待实测，#11 PR #13 审查补充第 2 条；ADR 0022）")]
    public async Task AHungOrderDoesNotGoOnByItselfOnceTheBrakeIsApplied()
    {
        var control = Target.RequireControl("松开再合上车的抱闸");
        var (upperId, order) = await ExecutingAsync("brake-applied-hang");
        await control.SetVehicleAsync(Facts.VehicleKey, brakeReleased: true);
        await UntilStateAsync(upperId, OrderStates.Hang);

        await control.SetVehicleAsync(Facts.VehicleKey, brakeReleased: false);
        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Hang, "仍挂起");
        await Riot.Tasks.HangContinueAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Success);
    }

    public enum ManualUse
    {
        BrakeReleased,
        ManualMode,
        ManualCharger,
    }

    private Task StartAsync(Targets.FakeRiotControl control, ManualUse use) => use switch
    {
        ManualUse.BrakeReleased => control.SetVehicleAsync(Facts.VehicleKey, brakeReleased: true),
        ManualUse.ManualMode => control.SetVehicleAsync(Facts.VehicleKey, mode: "MODE_MANUAL"),
        _ => control.SetVehicleAsync(Facts.VehicleKey, manualCharger: true),
    };

    private Task EndAsync(Targets.FakeRiotControl control, ManualUse use) => use switch
    {
        ManualUse.BrakeReleased => control.SetVehicleAsync(Facts.VehicleKey, brakeReleased: false),
        ManualUse.ManualMode => control.SetVehicleAsync(Facts.VehicleKey, mode: "MODE_AUTO"),
        _ => control.SetVehicleAsync(Facts.VehicleKey, manualCharger: false),
    };
}
