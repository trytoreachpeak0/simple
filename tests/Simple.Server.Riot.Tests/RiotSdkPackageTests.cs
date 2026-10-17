using System.Net;
using System.Text;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace Simple.Server.Riot.Tests;

// riot-sdk reaches Simple.Server.Riot as the GitHub Packages package RIoT.Sdk.Facade
// (ADR 0028), not as a project reference. This test goes through all three of
// its packages (Facade, the Kiota-generated client, Core) and the Kiota runtime
// on this repository's target framework, against a canned HTTP response; no
// fleet manager is contacted.
public sealed class RiotSdkPackageTests
{
    private const string SimpleInfoBody =
        """
        {"code":"0","message":"成功","result":[
          {"deviceKey":"BROKERX-0001","deviceName":"vehicle-1"},
          {"deviceKey":"BROKERX-0002","deviceName":"vehicle-2"}
        ]}
        """;

    [Fact]
    public async Task FacadeReadsDispatchableVehiclesThroughThePackage()
    {
        var handler = new CannedResponse(SimpleInfoBody);
        using var http = new HttpClient(handler);
        var options = new RiotOptions { BaseUrl = "http://riot.test", CallApiKey = "test-key" };
        await using var session = new RiotSession(options, http);

        var vehicles = await session.Tasks.GetDispatchableVehiclesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["BROKERX-0001", "BROKERX-0002"], vehicles.Select(v => v.DeviceKey));
        Assert.Equal("riot.test", handler.RequestedHost);
    }

    private sealed class CannedResponse(string body) : HttpMessageHandler
    {
        public string? RequestedHost { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedHost = request.RequestUri?.Host;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
