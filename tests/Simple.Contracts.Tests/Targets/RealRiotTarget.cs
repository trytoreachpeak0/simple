using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace Simple.Contracts.Tests.Targets;

/// <summary>
/// A real RIoT, read only: every contract that writes skips here
/// (<see cref="RiotTarget.RequireWrites"/>). Before the first call it says
/// on standard error which endpoint it is about to call.
/// </summary>
public sealed class RealRiotTarget : RiotTarget
{
    private readonly RealRiotSettings settings;
    private readonly RiotSession session;

    public RealRiotTarget(RealRiotSettings settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Console.Error.WriteLine(
            $"契约测试将调用真实 RIoT：{settings.Url}（只读；车 {settings.Facts.VehicleKey}，地图 {settings.Facts.MapId}；设置文件 {settings.SettingsFile}）");
        session = SessionWith(new RiotOptions { BaseUrl = BaseUrl, CallApiKey = settings.CallApiKey });
    }

    public override string Description => $"真实 RIoT {settings.Url}";

    public override string BaseUrl => settings.Url.ToString().TrimEnd('/');

    public override RiotTargetFacts Facts => settings.Facts;

    public override RiotSession Session => session;

    public override FakeRiotControl? Control => null;

    public override bool AllowsWrites => false;

    public override TimeSpan Patience => TimeSpan.FromSeconds(30);

    public override TimeSpan QuietWindow => TimeSpan.FromSeconds(10);

    public override RiotSession SessionWith(RiotOptions options) => new(options);

    public override HttpClient CreateRawClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(BaseUrl + "/") };

    public override async ValueTask DisposeAsync() => await session.DisposeAsync();
}
