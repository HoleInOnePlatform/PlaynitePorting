using System.Net;
using System.Net.Http;
using Playnite.HoleInOne;

static class HandoffRecoveryChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using (var wire = new Wire(false))
        using (var client = new BackendConnection("https://test.example/", "fixture", wire))
        {
            var status = await client.StartExportAsync("session", default);
            check(status.RequestId == "one" && wire.Posts == 2 && wire.Gets == 1,
                "502 before delivery checks existing export before submitting once more");
        }
        using (var wire = new Wire(true) { State = "failed" })
        using (var client = new BackendConnection("https://test.example/", "fixture", wire))
        {
            var status = await client.StartExportAsync("session", default);
            check(status.Status == "failed" && wire.Posts == 1 && wire.Gets == 1,
                "lost start response recovers existing failed export without silently restarting it");
        }
        using (var wire = new Wire(true) { PollFailure = true })
        using (var client = new BackendConnection("https://test.example/", "fixture", wire))
        {
            var status = await client.GetExportAsync("session", default);
            check(status.RequestId == "one" && wire.Gets == 2 && wire.Posts == 0,
                "transient status polling failure resumes the same export without POST");
        }
        using (var wire = new Wire(false) { Permanent = true })
        using (var client = new BackendConnection("https://test.example/", "fixture", wire))
        {
            try { await client.StartExportAsync("session", default); throw new Exception("403 accepted"); }
            catch (HttpRequestException) { check(wire.Posts == 1 && wire.Gets == 0, "permission errors are not retried"); }
        }
        using (var wire = new Wire(false))
        using (var client = new BackendConnection("https://test.example/", "fixture", wire))
        using (var cancel = new CancellationTokenSource(50))
        {
            try { await client.StartExportAsync("session", cancel.Token); throw new Exception("cancellation ignored"); }
            catch (OperationCanceledException) { check(wire.Posts == 1 && wire.Gets == 0, "closing the stream cancels recovery before any extra request"); }
        }
    }

    private sealed class Wire(bool accepted) : HttpMessageHandler
    {
        public int Posts, Gets;
        public string State = "exporting";
        public bool PollFailure, Permanent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpStatusCode code;
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                code = Permanent ? HttpStatusCode.Forbidden : Posts == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK;
            }
            else
                code = ++Gets == 1 && PollFailure ? HttpStatusCode.ServiceUnavailable :
                    !accepted && Posts < 2 ? HttpStatusCode.NotFound : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(code == HttpStatusCode.OK ?
                    "{\"requestId\":\"one\",\"gameId\":\"steam-2868840\",\"status\":\"" + State + "\"}" : "proxy-error-secret-must-not-be-shown")
            });
        }
    }
}
