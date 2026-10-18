using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace HoleInOne.SteamAuthProbe;

internal sealed class CloudProbeOptions
{
    public bool Enabled { get; }
    private readonly string? downloadUrl;
    public string? ExpectedHash { get; }
    public CloudProbeOptions()
    {
        Enabled = Take("HIO_PROBE_CLOUD") == "1";
        downloadUrl = Take("HIO_PROBE_BUNDLE_URL");
        ExpectedHash = Take("HIO_PROBE_BUNDLE_SHA256");
        // Do not propagate even the short-lived encrypted-object capability to Steam children.
    }
    private static string? Take(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, null);
        return value;
    }
    public async Task<byte[]> DownloadAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || ExpectedHash is null)
            throw new ProbeFailure("CLOUD_TRANSFER_NOT_CONFIGURED");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        return await CloudTransfer.DownloadAsync(client, uri, ExpectedHash, ct);
    }
}

internal sealed partial class ProbeWindow
{
    private readonly CloudProbeOptions cloud = new();
    private bool operatorConfirmedOnline;
    private static string CloudSteam => Path.Combine(AppContext.BaseDirectory, "Steam", "steam.exe");
    private static string CloudReport => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HoleInOneSteamAuthProbe", "result.json");

    private void SetupCloudUi()
    {
        Text = "HoleInOne - Isolated GameLift Steam Login Test";
        ClientSize = new Size(900, 520); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 12);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(24) };
        Controls.Add(panel);
        panel.Controls.Add(new Label { Text = "Reuse the encrypted file from your PC in this fresh cloud session.", AutoSize = true });
        panel.Controls.Add(new Label { Text = "Enter only the .hioauth file password. Do not enter your Steam password.\nNo QR or new Steam login is performed by this test.", AutoSize = true });
        panel.Controls.Add(ActionButton("1. Unlock saved authentication and test", CloudUnlockAndTest));
        panel.Controls.Add(ActionButton("2. Open Steam window", () => {
            if (nativeState != "expected_account_process_detected") throw new ProbeFailure("NATIVE_LOGIN_NOT_CONFIRMED");
            using var process = Process.Start(new ProcessStartInfo(CloudSteam) { UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(CloudSteam), Arguments = "-open steam://open/main" });
            return Task.CompletedTask;
        }));
        panel.Controls.Add(ActionButton("3. Confirm correct account is online", () => {
            if (nativeState != "expected_account_process_detected") throw new ProbeFailure("NATIVE_LOGIN_NOT_CONFIRMED");
            if (MessageBox.Show(this, "Did Steam log in to the expected account ONLINE without any additional password, QR or Guard approval?",
                "Manual verification", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                operatorConfirmedOnline = true;
                evidence.Add(new { operation = "operator_check", result = "expected_account_online_without_extra_auth", at = DateTimeOffset.UtcNow });
                state.Text = "Confirmation recorded. Use Finish and exit to end this test session.";
            }
            return Task.CompletedTask;
        }));
        cancel.Click += (_, _) => running?.Cancel(); panel.Controls.Add(cancel);
        var finish = new Button { Text = "4. Finish and exit", AutoSize = true };
        finish.Click += (_, _) => { if (running == null) { TryWriteCloudReport(); Close(); } };
        panel.Controls.Add(finish);
        state.Text = "Ready. The encrypted file is downloaded only when you click step 1. Results are saved automatically.";
        panel.Controls.Add(state);
        evidence.Add(new { operation = "cloud_probe", result = "ui_ready", at = DateTimeOffset.UtcNow });
        Shown += (_, _) => TryWriteCloudReport();
        FormClosing += (_, e) => { if (running != null) { running.Cancel(); e.Cancel = true; } else TryWriteCloudReport(); };
        FormClosed += (_, _) => credential = null;
    }

    private async Task CloudUnlockAndTest()
    {
        if (nativeState != "not_run") throw new ProbeFailure("START_A_NEW_CLOUD_SESSION");
        state.Text = "Downloading the encrypted test file...";
        var bytes = await cloud.DownloadAsync(running!.Token);
        transferHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = Input("Password for the .hioauth file (NOT Steam password)", true, running.Token);
        if (key == null) return;
        credential = await Task.Run(() => EncryptedTransfer.Open(bytes, key, DateTimeOffset.UtcNow), running.Token);
        evidence.Add(new { operation = "encrypted_import", result = "opened", transferSha256 = transferHash, at = DateTimeOffset.UtcNow });
        TryWriteCloudReport();
        await RunNative(CloudSteam);
    }

    private void TryWriteCloudReport()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CloudReport)!);
            var temporary = CloudReport + ".tmp";
            File.WriteAllText(temporary, ReportJson());
            File.Move(temporary, CloudReport, true);
        }
        catch { state.Text += " Report could not be saved; report the visible result code manually."; }
    }
}
