using System.Net;
using System.Text;
using Newtonsoft.Json;
using Playnite.HoleInOne;

static class SteamLinkChecks
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        foreach (var code in new[] { "NATIVE_LOGIN_NOT_CONFIRMED", "SELECT_STEAM_EXE", "STEAM_START_FAILED",
            "STEAM_TEMPORARILY_UNAVAILABLE", "cancelled_or_timeout", "CLEAN_TEST_PROFILE_REQUIRED", null })
            check(HoleInOne.SteamLink.CloudLoginStatus.ForFailure(code) == "failed",
                "cloud startup failure preserves linked credentials: " + (code ?? "transport failure"));
        check(HoleInOne.SteamLink.CloudLoginStatus.ForFailure("STEAM_LOGON_REJECTED") == "reauth_required",
            "explicit Steam credential rejection still requires reconnection");
        check(HoleInOne.SteamLink.CloudLoginStatus.ForFailure("NATIVE_ACCOUNT_MISMATCH") == "account_mismatch",
            "cloud account mismatch remains blocked");
        var linked = new SteamLinkInfo { State = "linked", SteamId = "76561197960265739", Version = "one" };
        linked.RequireCloudConnection();
        check(true, "cloud play requires only the server link, not PC Steam installation or a local library token");
        try { linked.RequireLibraryConnection(false); throw new Exception("missing library token accepted"); }
        catch (SteamLinkException e) { check(e.Code == "steam_local_library_link_required" && linked.IsLinked,
            "missing PC library token has a separate error and preserves the cloud link"); }
        linked.RequireLibraryConnection(true);
        foreach (var state in new[] { "unlinked", "reauth_required" })
        {
            try { new SteamLinkInfo { State = state }.RequireCloudConnection(); throw new Exception("unavailable link accepted"); }
            catch (SteamLinkException e) { check(e.Code == (state == "unlinked" ? "steam_link_required" : "steam_reauthentication_required"),
                "cloud entry distinguishes link state: " + state); }
        }
        using var wire=new Wire();
        using var insecure=new BackendConnection("http://localhost/","account-token",wire);
        try { await insecure.RegisterSteamLinkAsync("one","account",new string('x',80),default); throw new Exception("HTTP accepted"); }
        catch(SteamLinkException e) {check(e.Code=="https_required"&&wire.Calls==0,"client never sends Steam credentials over HTTP");}
        using var secure=new BackendConnection("https://service.example/","account-token",wire);
        var info=await secure.RegisterSteamLinkAsync("one","account",new string('x',80),default);
        check(info.IsLinked && wire.LastPath=="/v1/holeinone/steam-link"&&wire.LastMethod==HttpMethod.Put,"unified link uses authenticated registration endpoint");
        check(wire.Body.Contains("requestId")&&!wire.Body.Contains("ownerId")&&!wire.Body.Contains("steamId"),"client does not assign server identity or ownership");
        await secure.GetSteamLinkAsync(default);
        check(wire.LastMethod==HttpMethod.Get && wire.Body==null,"link lookup sends no saved token");
        wire.Status=HttpStatusCode.Conflict;wire.BodyToSend="{\"errorCode\":\"steam_session_in_use\",\"error\":\"secret-never-show\"}";
        try {await secure.PrepareSessionAsync("other-pc","steam-2868840",default);throw new Exception("conflict ignored");}
        catch(SteamLinkException e) {check(e.Code=="steam_session_in_use"&&!e.Message.Contains("secret-never-show"),"ownership conflict is surfaced without enabling legacy fallback or leaking backend body");}
        wire.Status=HttpStatusCode.Unauthorized;
        try {await secure.GetSteamLinkAsync(default);throw new Exception("expired account accepted");}
        catch(LoginRequiredException){check(true,"expired HoleInOne account requires login");}
        wire.Status=HttpStatusCode.OK;wire.BodyToSend="{\"state\":\"linked\",\"version\":\"one\",\"steamId\":\"invalid\"}";
        try {await secure.GetSteamLinkAsync(default);throw new Exception("invalid metadata accepted");}
        catch(System.IO.InvalidDataException){check(true,"invalid server Steam identity rejected");}
    }
    private sealed class Wire:HttpMessageHandler
    {
        public int Calls;public string Body,LastPath;public HttpMethod LastMethod;public HttpStatusCode Status=HttpStatusCode.OK;
        public string BodyToSend="{\"state\":\"linked\",\"version\":\"one\",\"steamId\":\"76561197960265739\"}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;LastPath=request.RequestUri.AbsolutePath;LastMethod=request.Method;
            if(request.Headers.Authorization?.Parameter!="account-token")throw new Exception("account bearer missing");
            Body=request.Content==null?null:await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(Status){Content=new StringContent(BodyToSend,Encoding.UTF8,"application/json")};
        }
    }
}
