using System.Net;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;
using RIoT.Sdk.Core;

namespace Simple.Contracts.Tests;

/// <summary>How RIoT accepts and refuses callers. Read only.</summary>
public sealed class AuthenticationContracts : ContractTest
{
    /// <summary>A wrong password is HTTP 200 with business code 2009: the HTTP status alone says nothing.</summary>
    [Fact]
    [Contract("BC-AUTH-001")]
    public async Task AWrongPasswordIsHttp200WithBusinessCode2009()
    {
        // A failed login against the real admin account might lock it; nobody knows.
        Assert.SkipUnless(Target.Control is not null, "在真实 RIoT 上故意用错的口令登录 admin，可能锁账号，默认不做。");
        if (Facts.Username is null)
        {
            Assert.Skip("设置文件没有给出登录用户名。");
        }

        await using var session = Target.SessionWith(new RiotOptions
        {
            BaseUrl = Target.BaseUrl,
            Username = Facts.Username,
            Password = Facts.Password + "-wrong",
        });

        var refused = await Assert.ThrowsAsync<RiotApiException>(() => session.LoginAsync(Cancel));

        Assert.Equal(200, refused.StatusCode);
        Assert.Equal("2009", refused.BusinessCode);
    }

    [Fact]
    [Contract("BC-AUTH-001")]
    public async Task TheLoginTokenOpensTheBusinessCalls()
    {
        if (Facts.Username is null)
        {
            Assert.Skip("设置文件没有给出登录用户名。");
        }

        await using var session = Target.SessionWith(new RiotOptions
        {
            BaseUrl = Target.BaseUrl,
            Username = Facts.Username,
            Password = Facts.Password,
        });

        var tokens = await session.LoginAsync(Cancel);
        var maps = await session.Maps.ListMapsAsync(Cancel);

        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.Contains(maps, m => m.MapId == Facts.MapId);
    }

    /// <summary>The web page's call key is the Bearer; a forged one makes a business call HTTP 401.</summary>
    [Fact]
    [Contract("BC-AUTH-002")]
    public async Task AForgedCallKeyIsHttp401()
    {
        await using var session = Target.SessionWith(new RiotOptions { BaseUrl = Target.BaseUrl, CallApiKey = "forged-call-api-key" });

        var refused = await Assert.ThrowsAsync<ApiException>(() => session.Maps.ListMapsAsync(Cancel));

        Assert.Equal(401, refused.ResponseStatusCode);
    }

    /// <summary>
    /// Without any Authorization header, too. The facade refuses to send a
    /// call without a credential, so this one request goes out as plain HTTP.
    /// </summary>
    [Fact]
    [Contract("BC-AUTH-002")]
    public async Task NoAuthorizationIsHttp401WithBusinessCode401()
    {
        using var http = Target.CreateRawClient();

        using var response = await http.GetAsync(new Uri("api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson", UriKind.Relative), Cancel);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("401", body.RootElement.GetProperty("code").ToString());
    }
}
