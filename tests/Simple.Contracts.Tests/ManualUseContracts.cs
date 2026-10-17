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

    /// <summary>
    /// With the brake still released, CONTINUE_FROM_HANG is refused with 14013
    /// (车辆状态异常) and the order stays hung; once the brake is applied the
    /// same command is accepted.
    /// </summary>
    [Fact]
    [Contract("round 27", "Q-035", "BC-ORDER-015")]
    public async Task ContinuingWhileTheBrakeIsStillReleasedIsRefusedWith14013()
    {
        var control = Target.RequireControl("松开车的抱闸");
        var (upperId, order) = await ExecutingAsync("brake-continue");
        await control.SetVehicleAsync(Facts.VehicleKey, brakeReleased: true);
        await UntilStateAsync(upperId, OrderStates.Hang);

        var refused = await Assert.ThrowsAsync<RIoT.Sdk.Core.RiotApiException>(() => Riot.Tasks.HangContinueAsync(order.OrderId, cancellationToken: Cancel));

        Assert.Equal("14013", refused.BusinessCode);
        Assert.Equal(OrderStates.Hang, (await ReadAsync(upperId)).OrderState);
    }

    /// <summary>An order made while the brake is released stays queued.</summary>
    [Fact]
    [Contract("round 29-F", "round 30", "#11", "ADR 0022")]
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

    /// <summary>
    /// Driven from Matrix, the vehicle moves with no order: MT_RUNNING with
    /// procState IDLE (round 38, the lab's "单机去站点"), while its mode still
    /// reads MODE_AUTO. That Matrix is what the lab calls 单机 (stand-alone
    /// operation on the vehicle) is an inference: ADR 0010 and ADR 0022 say
    /// "Matrix", the lab says 单机, and both name what is done on the vehicle itself.
    /// </summary>
    [Fact]
    [Contract("ADR 0022", "#11", "round 38", "Q-039")]
    [NotVerifiedOnRealRiot("在 Matrix 上让车走时 mode 仍是 MODE_AUTO，车在动、车上没有订单（推测；2026-10-11 现场一个样本，没记下操作方式；ADR 0022）。MT_RUNNING、procState IDLE 第 38 轮单机去站点见过")]
    public async Task DrivenFromMatrixTheVehicleMovesWithoutAnOrderInAutoMode()
    {
        var control = Target.RequireControl("在车上的 Matrix 里让车走");

        await control.DriveAsync(Facts.VehicleKey, Facts.ReachableStation);
        var moving = await UntilAsync(_ => VehicleAsync(), v => v.Speed > 0, "车在动");

        Assert.Equal("MODE_AUTO", moving.Mode);
        Assert.Equal("IDLE", moving.ProcState);
        Assert.Equal("MT_RUNNING", moving.MovementState);
        Assert.Null((await CardAsync()).OrderTaskId);
    }

    /// <summary>
    /// An order made while a person drives the vehicle from Matrix is queued
    /// and does not take over; it starts once the vehicle has arrived
    /// (round 38: about 2 s after MT_FINISHED).
    /// </summary>
    [Fact]
    [Contract("BC-ORDER-018", "round 38", "Q-039")]
    public async Task AnOrderMadeWhileAPersonDrivesTheVehicleWaitsUntilItArrives()
    {
        var control = Target.RequireControl("在车上的 Matrix 里让车去站点");
        await control.DriveAsync(Facts.VehicleKey, Facts.ReachableStation);
        await UntilAsync(_ => VehicleAsync(), v => v.Speed > 0, "车在动");
        var upperId = NewUpperId("behind-matrix");

        await CreateMoveAsync(upperId, Facts.SecondReachableStation);
        var created = DateTimeOffset.UtcNow;
        var queuedWhileMoving = TimeSpan.Zero;
        await UntilAsync(
            async _ =>
            {
                var reading = (Order: (await ReadAsync(upperId)).OrderState, Vehicle: await VehicleAsync());
                if (reading is { Order: OrderStates.Queueing, Vehicle: { Speed: > 0, ProcState: "IDLE", MovementState: "MT_RUNNING" } })
                {
                    queuedWhileMoving = DateTimeOffset.UtcNow - created;
                }

                return reading;
            },
            r => r.Order != OrderStates.Queueing,
            $"订单 {upperId} 开始执行");

        // The drive takes a second in the fake; an order that took over would start within its 50 ms queue time.
        Assert.True(queuedWhileMoving > TimeSpan.FromMilliseconds(400), $"车在动时订单只排了 {queuedWhileMoving.TotalMilliseconds} ms 就不再排队。");
        await UntilStateAsync(upperId, OrderStates.Success);
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

    /// <summary>A held order can be cancelled while the vehicle is in manual use.</summary>
    [Theory]
    [InlineData(ManualUse.BrakeReleased)]
    [InlineData(ManualUse.ManualMode)]
    [InlineData(ManualUse.ManualCharger)]
    [Contract("ADR 0022", "#11")]
    [NotVerifiedOnRealRiot("抱闸松着、MODE_MANUAL、插着手动充电器时，对暂停的订单发取消能生效（待实测，#11 PR #13 审查补充第 1 条；ADR 0022）")]
    public async Task AHeldOrderCanBeCancelledDuringManualUse(ManualUse use)
    {
        var control = Target.RequireControl("人手动用车（松抱闸、切手动、插手动充电器）");
        var (upperId, order) = await HeldAsync("manual-cancel-held");
        await StartAsync(control, use);

        await Riot.Tasks.CancelOrderAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Cancelled);
    }

    /// <summary>A hung order can be cancelled while the vehicle is in manual use.</summary>
    [Theory]
    [InlineData(ManualUse.BrakeReleased)]
    [InlineData(ManualUse.ManualMode)]
    [InlineData(ManualUse.ManualCharger)]
    [Contract("ADR 0022", "#11")]
    [NotVerifiedOnRealRiot("抱闸松着、MODE_MANUAL、插着手动充电器时，对挂起的订单发取消能生效（待实测，#11 PR #13 审查补充第 1 条；ADR 0022）")]
    public async Task AHungOrderCanBeCancelledDuringManualUse(ManualUse use)
    {
        var control = Target.RequireControl("人手动用车（松抱闸、切手动、插手动充电器）");
        var (upperId, order) = await HungAsync(control, use, "manual-cancel-hang");

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

    /// <summary>
    /// A hung order does not go on by itself once the manual use ends: it
    /// needs CONTINUE_FROM_HANG, which is then accepted (for the brake, round
    /// 27 F saw the continue accepted once the brake was applied).
    /// </summary>
    [Theory]
    [InlineData(ManualUse.BrakeReleased)]
    [InlineData(ManualUse.ManualMode)]
    [InlineData(ManualUse.ManualCharger)]
    [Contract("ADR 0022", "#11", "round 27")]
    [NotVerifiedOnRealRiot("切回自动模式、合上抱闸、拔下手动充电器以后，挂起的单不会自己继续，要发从挂起继续（待实测，#11 PR #13 审查补充第 2 条；ADR 0022）；合上抱闸后从挂起继续回 0，第 27 轮 F 见过")]
    public async Task AHungOrderDoesNotGoOnByItselfOnceManualUseEnds(ManualUse use)
    {
        var control = Target.RequireControl("人手动用车，用完恢复");
        var (upperId, order) = await HungAsync(control, use, "manual-ends-hang");

        await EndAsync(control, use);
        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Hang, "仍挂起");
        await Riot.Tasks.HangContinueAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Success);
    }

    /// <summary>A held order stays held once the manual use ends: it needs CONTINUE_FROM_HELD.</summary>
    [Theory]
    [InlineData(ManualUse.BrakeReleased)]
    [InlineData(ManualUse.ManualMode)]
    [InlineData(ManualUse.ManualCharger)]
    [Contract("ADR 0022", "#11")]
    [NotVerifiedOnRealRiot("切回自动模式、合上抱闸、拔下手动充电器以后，暂停的单不会自己继续（待实测，#11 PR #13 审查补充第 2 条；ADR 0022）")]
    public async Task AHeldOrderStaysHeldOnceManualUseEnds(ManualUse use)
    {
        var control = Target.RequireControl("人手动用车，用完恢复");
        var (upperId, order) = await HeldAsync("manual-ends-held");
        await StartAsync(control, use);

        await EndAsync(control, use);
        await ThroughoutAsync(_ => ReadAsync(upperId), o => o.OrderState == OrderStates.Held, "仍暂停");
        await Riot.Tasks.OrderContinueAsync(order.OrderId, cancellationToken: Cancel);

        await UntilStateAsync(upperId, OrderStates.Success);
    }

    public enum ManualUse
    {
        BrakeReleased,
        ManualMode,
        ManualCharger,
    }

    private async Task<(string UpperId, RIoT.Sdk.Core.OrderSnapshot Order)> HeldAsync(string tag)
    {
        var (upperId, order) = await ExecutingAsync(tag);
        await Riot.Tasks.OrderHoldAsync(order.OrderId, cancellationToken: Cancel);
        await UntilStateAsync(upperId, OrderStates.Held);
        return (upperId, order);
    }

    /// <summary>
    /// A hung order with the manual use under way. Releasing the brake hangs
    /// the order by itself (round 27 F); for the other two the order is hung as
    /// a cancel on the vehicle's own screen does (round 27 E), then the use starts.
    /// </summary>
    private async Task<(string UpperId, RIoT.Sdk.Core.OrderSnapshot Order)> HungAsync(Targets.FakeRiotControl control, ManualUse use, string tag)
    {
        var (upperId, order) = await ExecutingAsync(tag);
        if (use != ManualUse.BrakeReleased)
        {
            await control.HangAsync(order.OrderId);
        }

        await StartAsync(control, use);
        await UntilStateAsync(upperId, OrderStates.Hang);
        return (upperId, order);
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
