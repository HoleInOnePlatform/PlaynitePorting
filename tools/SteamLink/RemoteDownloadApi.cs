using System.Globalization;
using System.Text.Json;

namespace HoleInOne.SteamLink;

// ClientComm is Steam's internal remote-download interface, not a supported public Steamworks API.
// Protocol: https://github.com/SteamTracking/Protobufs/blob/master/webui/service_clientcomm.proto
internal sealed class RemoteDownloadApi(HttpClient http, string accessToken)
{
    internal async Task RequestAsync(uint appId, bool uninstall, string machineName, ISet<string> localAddresses,
        Action requireLocalClient, CancellationToken ct)
    {
        if (appId == 0) throw new ProbeFailure("REMOTE_INVALID_APP");
        requireLocalClient();
        using var sessions = await SendAsync("GetAllClientLogonInfo", null, ct);
        var candidates = new List<ulong>();
        if (sessions.RootElement.GetProperty("response").TryGetProperty("sessions", out var list))
        {
            foreach (var session in list.EnumerateArray())
            {
                if (!session.TryGetProperty("machine_name", out var name) ||
                    !string.Equals(name.GetString(), machineName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!session.TryGetProperty("client_instanceid", out var instance) ||
                    !ulong.TryParse(instance.ToString(), out var id) || id == 0) continue;
                using var info = await SendAsync("GetClientInfo", new() { ["client_instanceid"] = id.ToString(CultureInfo.InvariantCulture) }, ct);
                if (info.RootElement.GetProperty("response").TryGetProperty("client_info", out var details) &&
                    details.TryGetProperty("machine_name", out var detailName) &&
                    string.Equals(detailName.GetString(), machineName, StringComparison.OrdinalIgnoreCase) &&
                    details.TryGetProperty("ip_private", out var ip) && localAddresses.Contains(ip.GetString() ?? ""))
                    candidates.Add(id);
            }
        }
        if (candidates.Count != 1) throw new ProbeFailure(candidates.Count == 0 ? "REMOTE_CLIENT_NOT_FOUND" : "REMOTE_CLIENT_AMBIGUOUS");
        ct.ThrowIfCancellationRequested();
        requireLocalClient();
        // No folder, volume index or fallback target. Never retry a mutation after an uncertain result.
        using var response = await SendAsync(uninstall ? "UninstallClientApp" : "InstallClientApp", new()
        {
            ["appid"] = appId.ToString(CultureInfo.InvariantCulture),
            ["client_instanceid"] = candidates[0].ToString(CultureInfo.InvariantCulture)
        }, ct, mutation: true);
    }

    private async Task<JsonDocument> SendAsync(string method, Dictionary<string, string>? parameters, CancellationToken ct, bool mutation = false)
    {
        SteamDiagnostics.Write(method, "started");
        parameters ??= new();
        parameters["access_token"] = accessToken;
        // Read methods use GET as required by Web API. Never expose transport exceptions/URLs.
        var endpoint = "https://api.steampowered.com/IClientCommService/" + method + "/v1/";
        using var form = new FormUrlEncodedContent(parameters);
        using var request = mutation
            ? new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form }
            : new HttpRequestMessage(HttpMethod.Get, endpoint + "?" + await form.ReadAsStringAsync(ct));
        try
        {
            using var response = await http.SendAsync(request, ct);
            var diagnosticResult = response.Headers.TryGetValues("x-eresult", out var diagnosticValues) &&
                int.TryParse(diagnosticValues.FirstOrDefault(), out var numericResult) ? (int?)numericResult : null;
            SteamDiagnostics.Write(method, "response", diagnosticResult, (int)response.StatusCode);
            if (!response.IsSuccessStatusCode) throw new ProbeFailure("REMOTE_REQUEST_FAILED");
            var hasResult = response.Headers.TryGetValues("x-eresult", out var values);
            if (hasResult && values!.SingleOrDefault() != "1") throw new ProbeFailure("REMOTE_REQUEST_FAILED");
            var body = await response.Content.ReadAsStringAsync(ct);
            var parsed = JsonDocument.Parse(body);
            if (!parsed.RootElement.TryGetProperty("response", out var result) || result.ValueKind != JsonValueKind.Object ||
                (mutation && !hasResult))
            { parsed.Dispose(); throw new ProbeFailure("REMOTE_UNCONFIRMED"); }
            return parsed;
        }
        catch (HttpRequestException) { SteamDiagnostics.Write(method, "transport_failed"); throw new ProbeFailure("REMOTE_REQUEST_FAILED"); }
        catch (JsonException) { SteamDiagnostics.Write(method, "invalid_json"); throw new ProbeFailure("REMOTE_UNCONFIRMED"); }
    }
}
