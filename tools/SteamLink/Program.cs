using HoleInOne.SteamLink;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

// Only the private stdin/stdout pipes carry transient credentials. No command-line secrets or exception bodies.
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var outputGate = new object();
void Emit(object value) { lock(outputGate) Console.WriteLine(JsonSerializer.Serialize(value,json)); }
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var commands = Channel.CreateUnbounded<Command>(new() { SingleReader=true, SingleWriter=true });
_ = Task.Run(async () => {
    try {
        while(!lifetime.IsCancellationRequested) {
            var line=await Console.In.ReadLineAsync(lifetime.Token);
            if(line==null)break;
            if(line.Length>32768)throw new InvalidDataException();
            var command=JsonSerializer.Deserialize<Command>(line,json)??throw new InvalidDataException();
            if(command.Operation is "cancel" or "quit")break;
            await commands.Writer.WriteAsync(command,lifetime.Token);
        }
    } catch(Exception) { }
    finally { commands.Writer.TryComplete(); lifetime.Cancel(); }
});
try
{
    if(args.SequenceEqual(new[]{"--cloud"})) await CloudAsync();
    else
    {
        var command=await commands.Reader.ReadAsync(lifetime.Token);
        if(command.Operation is "library" or "install" or "uninstall")
        {
            if(command.TokenPath == null || !Path.IsPathFullyQualified(command.TokenPath)) throw new InvalidDataException();
            var libraryPlain = ProtectedData.Unprotect(File.ReadAllBytes(command.TokenPath), null, DataProtectionScope.CurrentUser);
            Credential saved;
            try { saved = JsonSerializer.Deserialize<Credential>(libraryPlain) ?? throw new InvalidDataException(); }
            finally { CryptographicOperations.ZeroMemory(libraryPlain); }
            if(saved.SteamId.ToString() != command.SteamId) throw new ProbeFailure("STEAM_ACCOUNT_MISMATCH");
            if(command.Operation is "install" or "uninstall")
            {
                await RemoteDownloads.RunAsync(saved, command.AppId, command.Operation == "uninstall", lifetime.Token);
                Emit(new { type = "accepted", steamId = saved.SteamId.ToString(), appId = command.AppId, operation = command.Operation });
                return;
            }
            await using var session = new SteamAuthentication();
            await session.ValidateAsync(saved, lifetime.Token);
            Emit(new { type = "library", steamId = saved.SteamId.ToString(), games = await session.OwnedGamesAsync(lifetime.Token) });
            return;
        }
        if(command.Operation!="connect" || command.Method is not ("qr" or "password"))throw new InvalidDataException();
        await using var auth=new SteamAuthentication();
        var credential=await auth.AuthenticateAsync(command.Method=="qr",command.Username??"",command.Password??"",
            bytes=>Emit(new{type="qr",png=Convert.ToBase64String(bytes)}),async prompt=>{
                Emit(new{type="guard",email=prompt.Email,incorrect=prompt.Incorrect});
                var answer=await commands.Reader.ReadAsync(lifetime.Token);
                if(answer.Operation!="guard" || answer.Code==null || answer.Code.Length>64)throw new InvalidDataException();
                return answer.Code;
            },lifetime.Token);
        command=command with {Password=null};
        Emit(new{type="authenticated",accountName=credential.AccountName,refreshToken=credential.RefreshToken,steamId=credential.SteamId.ToString()});
        // A server-accepted version is required before replacing an existing local login.
        var commit=await commands.Reader.ReadAsync(lifetime.Token);
        if(commit.Operation!="commit" || commit.SteamId!=credential.SteamId.ToString() ||
            string.IsNullOrEmpty(commit.TokenPath) || !Path.IsPathFullyQualified(commit.TokenPath))throw new InvalidDataException();
        var plain=JsonSerializer.SerializeToUtf8Bytes(credential);
        try
        {
            var encrypted=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(commit.TokenPath)!);
            var temp=commit.TokenPath+"."+Guid.NewGuid().ToString("N")+".part";
            try { File.WriteAllBytes(temp,encrypted); File.Move(temp,commit.TokenPath,true); }
            finally { if(File.Exists(temp))File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Emit(new{type="saved"});
    }
}
catch(Exception error)
{
    Emit(new{type="error",code=error is ProbeFailure p ? p.Code : error is OperationCanceledException ? "cancelled_or_timeout" : "steam_link_failed"});
    Environment.ExitCode=1;
}
finally { lifetime.Cancel(); }

async Task CloudAsync()
{
    var root=Environment.GetEnvironmentVariable("GAMELINK_BACKEND_URL");
    var id=Environment.GetEnvironmentVariable("GAMELINK_SESSION_ID");
    var token=Environment.GetEnvironmentVariable("HOLEINONE_STEAM_BOOTSTRAP_TOKEN");
    Environment.SetEnvironmentVariable("HOLEINONE_STEAM_BOOTSTRAP_TOKEN",null);
    Environment.SetEnvironmentVariable("HOLEINONE_CONTROL_TOKEN",null);
    Environment.SetEnvironmentVariable("HOLEINONE_CLOUD_AGENT_TOKEN",null);
    if(!Uri.TryCreate(root,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo.Length!=0 || uri.AbsolutePath!="/" || uri.Query.Length!=0 || uri.Fragment.Length!=0 ||
        id==null || !System.Text.RegularExpressions.Regex.IsMatch(id,"\\A[a-zA-Z0-9_-]{1,80}\\z") || token?.Length!=64)throw new InvalidDataException();
    using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=uri,Timeout=TimeSpan.FromSeconds(30)};
    http.DefaultRequestHeaders.Authorization=new("Bearer",token);
    var endpoint="v1/cloud-agent/sessions/"+id;
    async Task<HttpResponseMessage> RetryPostAsync(string path, object? body)
    {
        for(var attempt=0;;attempt++)
        {
            try {
                var result=body==null ? await http.PostAsync(path,null,lifetime.Token) : await http.PostAsJsonAsync(path,body,lifetime.Token);
                if(attempt>=2 || ((int)result.StatusCode<500 && result.StatusCode!=System.Net.HttpStatusCode.Conflict)) return result;
                result.Dispose();
            } catch(HttpRequestException) when(attempt<2) { }
            await Task.Delay(1000,lifetime.Token);
        }
    }
    try
    {
        using var response=await RetryPostAsync(endpoint+"/steam-bootstrap",null);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>32768)throw new InvalidDataException();
        using var stream=await response.Content.ReadAsStreamAsync(lifetime.Token);
        using var buffer=new MemoryStream();
        var chunk=new byte[4096]; int read;
        while((read=await stream.ReadAsync(chunk,lifetime.Token))!=0) {
            if(buffer.Length+read>32768)throw new InvalidDataException(); buffer.Write(chunk,0,read);
        }
        var bytes=buffer.ToArray(); CryptographicOperations.ZeroMemory(buffer.GetBuffer());
        Credential credential;
        try{ if(bytes.Length>32768)throw new InvalidDataException(); credential=JsonSerializer.Deserialize<Credential>(bytes,json)??throw new InvalidDataException(); }
        finally{CryptographicOperations.ZeroMemory(bytes);}
        var command=await commands.Reader.ReadAsync(lifetime.Token);
        if(command.Operation!="native" || command.SteamPath==null)throw new InvalidDataException();
        await NativeLogin.RunAsync(credential,command.SteamPath,_=>{},lifetime.Token);
        using var report=await RetryPostAsync(endpoint+"/steam-status",new{status="ready",actualSteamId=credential.SteamId.ToString()});
        report.EnsureSuccessStatusCode();
        Emit(new{type="ready",steamId=credential.SteamId.ToString()});
    }
    catch(Exception error)
    {
        var status=error is ProbeFailure failure ? failure.Code switch {
            "STEAM_LOGON_REJECTED" or "NATIVE_LOGIN_NOT_CONFIRMED" => "reauth_required",
            "NATIVE_ACCOUNT_MISMATCH" or "STEAM_ACCOUNT_MISMATCH" => "account_mismatch",_=>"failed"} : "failed";
        try { using var report=await http.PostAsJsonAsync(endpoint+"/steam-status",new{status,actualSteamId=(string?)null},CancellationToken.None); } catch(Exception) { }
        throw;
    }
}
record Command(string Operation,string? Method=null,string? Username=null,string? Password=null,string? Code=null,string? TokenPath=null,string? SteamId=null,string? SteamPath=null,uint AppId=0);
