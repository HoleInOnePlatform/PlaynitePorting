using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HoleInOne.SteamLink;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows IPC regression must run on Windows.");
var marker = Environment.GetEnvironmentVariable("HIO_PROCESS_CHECK_MARKER");
if (args.SequenceEqual(new[] { "-silent" }))
{
    // Represents the real Steam GUI child writing diagnostic output.
    Console.Write("Steam child output"); Console.Out.Flush();
    File.WriteAllText(marker!, "started");
    Console.WriteLine(new string('x', 128 * 1024));
    return;
}
if (args.SequenceEqual(new[] { "--worker" }))
{
    using var native = NativeSteamProcess.Start(Environment.ProcessPath!);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!File.Exists(marker)) await Task.Delay(10, timeout.Token);
    Console.WriteLine("{\"type\":\"ready\"}");
    await native.WaitForExitAsync(timeout.Token);
    return;
}
marker = Path.Combine(Path.GetTempPath(), "hio-ipc-" + Guid.NewGuid().ToString("N"));
using var worker = new Process { StartInfo = new(Environment.ProcessPath!) {
    Arguments = "--worker", UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
} };
worker.StartInfo.Environment["HIO_PROCESS_CHECK_MARKER"] = marker;
try
{
    worker.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var frame = new MemoryStream();
    var one = new byte[1];
    for (;;)
    {
        if (await worker.StandardOutput.BaseStream.ReadAsync(one, timeout.Token) == 0) throw new Exception("Missing helper reply");
        if (frame.Length == 0 && one[0] != '{') throw new Exception("Steam output inherited the private helper pipe");
        if (one[0] == '\n') break;
        if (frame.Length >= 4096) throw new Exception("Protocol frame polluted by child output");
        frame.WriteByte(one[0]);
    }
    using var reply = JsonDocument.Parse(frame.ToArray());
    if (reply.RootElement.GetProperty("type").GetString() != "ready") throw new Exception("Incorrect helper reply");
    worker.StandardInput.Close();
    await worker.WaitForExitAsync(timeout.Token);
    if (worker.ExitCode != 0) throw new Exception("Helper did not exit cleanly");
    if ((await worker.StandardOutput.ReadToEndAsync(timeout.Token)).Length != 0) throw new Exception("Native output leaked after reply");
    Console.WriteLine("PASS Windows native child output cannot corrupt or block the private helper pipe");
}
finally
{
    try { if (!worker.HasExited) { worker.Kill(true); worker.WaitForExit(3000); } } catch (InvalidOperationException) { }
    if (File.Exists(marker)) File.Delete(marker);
}
