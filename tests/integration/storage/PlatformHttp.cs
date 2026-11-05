using System.Net;
using System.Text.Json;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class PlatformHttp(bool lostResponse) : HttpMessageHandler
{
    private readonly HashSet<string> _signed = [];
    internal int Submissions { get; private set; }
    internal int Rotations { get; private set; }
    internal HashSet<string> Origins { get; } = [];
    internal Action? BeforeSubmission { get; set; }
    internal Action? AfterRotation { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!; Origins.Add(uri.Host);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        object result = uri.Host switch
        {
            "bbs-api.miyoushe.com" or "bbs-api-os.hoyolab.com" or "api-takumi.mihoyo.com" or "act-nap-api.mihoyo.com" or "sg-hk4e-api.hoyolab.com" or
                "sg-public-api.hoyolab.com" or "sg-act-nap-api.hoyolab.com" => Hoyo(request, body),
            "as.hypergryph.com" or "as.gryphline.com" => Grant(request, body),
            "zonai.skland.com" or "zonai.skport.com" => Passport(request, body),
            "fp-it.portal101.cn" => new { code = 1100, detail = new { deviceId = "synthetic-device" } },
            "api.kurobbs.com" => Kuro(request, body),
            "bbs-api.tajiduo.com" => Tajiduo(request, body),
            _ => throw new IOException("unexpected_checkin_origin:" + uri.Host)
        };
        return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(result),
            System.Text.Encoding.UTF8, "application/json") };
    }

    private object Hoyo(HttpRequestMessage request, string body)
    {
        var uri = request.RequestUri!;
        Assert.Contains("synthetic-", Assert.Single(request.Headers.GetValues("Cookie")), StringComparison.Ordinal);
        Assert.True(request.Headers.Contains("x-rpc-signgame"));
        if (uri.AbsolutePath is "/user/wapi/getUserFullInfo" or "/community/painter/wapi/user/full")
            return new { retcode = 0, data = new { user_info = new { uid = "123456" } } };
        if (uri.Host.EndsWith("mihoyo.com", StringComparison.Ordinal))
        {
            Assert.Equal(3, Assert.Single(request.Headers.GetValues("DS")).Split(',').Length);
            Assert.True(Guid.TryParse(Assert.Single(request.Headers.GetValues("x-rpc-device_id")), out _));
        }
        if (uri.AbsolutePath == "/binding/api/getUserGameRolesByCookieToken")
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return new { retcode = 0, data = new { list = new[] { new { game_uid = "789123", region = "cn_gf01" } } } };
        }
        if (uri.AbsolutePath.EndsWith("/info", StringComparison.Ordinal))
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var act = Query(uri.Query)["act_id"];
            return new { retcode = 0, data = new { is_sign = _signed.Contains(uri.Host + ":" + act) } };
        }
        Assert.EndsWith("/sign", uri.AbsolutePath, StringComparison.Ordinal); Assert.Equal(HttpMethod.Post, request.Method);
        using var document = JsonDocument.Parse(body); var root = document.RootElement;
        if (uri.Host.EndsWith("mihoyo.com", StringComparison.Ordinal)) Assert.Equal("789123", root.GetProperty("uid").GetString());
        Submit(uri.Host + ":" + root.GetProperty("act_id").GetString());
        return new { retcode = 0, data = new { } };
    }

    private static object Grant(HttpRequestMessage request, string body)
    {
        Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/user/oauth2/v2/grant", request.RequestUri!.AbsolutePath);
        using var document = JsonDocument.Parse(body);
        Assert.StartsWith("synthetic-", document.RootElement.GetProperty("token").GetString(), StringComparison.Ordinal);
        return new { status = 0, data = new { code = "synthetic-grant" } };
    }

    private object Passport(HttpRequestMessage request, string body)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath == "/web/v1/user/auth/generate_cred_by_code")
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            return new { code = 0, data = new { cred = "synthetic-cred", token = "synthetic-signing-key" } };
        }
        Assert.Equal("synthetic-cred", Assert.Single(request.Headers.GetValues("cred")));
        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(request.Headers.GetValues("sign")));
        if (uri.AbsolutePath == "/web/v1/user") return new { code = 0, data = new { user = new { id = "123456" } } };
        if (uri.AbsolutePath == "/web/v2/user") return new { code = 0, data = new { user = new { basicUser = new { id = "123456" } } } };
        if (uri.AbsolutePath == "/api/v1/game/player/binding")
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return new { code = 0, data = new { list = new[] {
                new { appCode = "arknights", bindingList = new[] { new { uid = "123456", channelMasterId = "1", roles = Array.Empty<object>() } } },
                new { appCode = "endfield", bindingList = new[] { new { uid = "123456", channelMasterId = "1", roles = new object[] { new { roleId = "789123", serverId = "1" } } } } }
            } } };
        }
        Assert.Equal(HttpMethod.Post, request.Method);
        if (uri.AbsolutePath == "/api/v1/game/endfield/attendance")
        { Assert.Equal("3_789123_1", Assert.Single(request.Headers.GetValues("sk-game-role"))); Assert.Empty(body); }
        else
        {
            Assert.Equal("/api/v1/game/attendance", uri.AbsolutePath);
            using var document = JsonDocument.Parse(body); Assert.Equal("123456", document.RootElement.GetProperty("uid").GetString());
        }
        Submit(uri.Host + uri.AbsolutePath); return new { code = 0 };
    }

    private object Kuro(HttpRequestMessage request, string body)
    {
        Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("synthetic-private-token", Assert.Single(request.Headers.GetValues("token")));
        var form = Query(body);
        if (request.RequestUri!.AbsolutePath == "/user/mineV2")
        { Assert.Equal("1", form["type"]); return new { code = 200, data = new { mine = new { userId = "123456" } } }; }
        Assert.Contains(form["gameId"], new[] { "2", "3" });
        if (request.RequestUri!.AbsolutePath == "/user/role/findRoleList")
            return new { code = 200, data = new[] { new { gameId = form["gameId"], roleId = "789123", serverId = "1", userId = "123456" } } };
        Assert.Equal("/encourage/signIn/v2", request.RequestUri.AbsolutePath); Assert.Equal("789123", form["roleId"]);
        Submit("kuro:" + form["gameId"]); return new { code = 200 };
    }

    private object Tajiduo(HttpRequestMessage request, string body)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath == "/usercenter/api/refreshTokenV2")
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("synthetic-sdk-token", Query(body)["token"]); Rotations++;
            return new { code = 0, data = new { accessToken = "synthetic-rotated-access", refreshToken = "synthetic-rotated-refresh", uid = "123456" } };
        }
        AfterRotation?.Invoke();
        Assert.Contains(Assert.Single(request.Headers.GetValues(uri.AbsolutePath.Contains("getGameRoles", StringComparison.Ordinal) ? "AuthorizationV2" : "Authorization")),
            new[] { "synthetic-access", "synthetic-rotated-access" });
        if (uri.AbsolutePath == "/usercenter/api/v2/getGameRoles")
            return new { code = 0, data = new { roles = new[] { new { roleId = "789123" } } } };
        if (uri.AbsolutePath == "/apihub/awapi/signin/state") return new { code = 0, data = new { todaySign = _signed.Contains("tajiduo:nte") } };
        Assert.Equal("/apihub/awapi/sign", uri.AbsolutePath); Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("789123", Query(body)["roleId"]); Submit("tajiduo:nte"); return new { code = 0 };
    }

    private void Submit(string key)
    {
        Assert.DoesNotContain(key, _signed); BeforeSubmission?.Invoke(); _signed.Add(key); Submissions++;
        if (lostResponse) throw new HttpRequestException("fixture_response_lost_after_submission");
    }
    private static Dictionary<string, string> Query(string value) => value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
}
