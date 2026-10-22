using System.Security.Cryptography;
using System.Text.Json;

namespace HoleInOne.SteamAuthProbe;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ProbeWindow());
    }
}

internal sealed partial class ProbeWindow : Form
{
    private readonly Label state = new() { AutoSize = true, MaximumSize = new Size(670, 0), Text = "신규 인증을 선택하세요. 기존 Steam 인증 파일은 읽지 않습니다." };
    private readonly PictureBox qr = new() { Size = new Size(280, 280), SizeMode = PictureBoxSizeMode.Zoom };
    private readonly TextBox username = new() { Width = 220, PlaceholderText = "Steam 로그인 이름" };
    private readonly TextBox password = new() { Width = 220, PlaceholderText = "Steam 비밀번호", UseSystemPasswordChar = true };
    private readonly Button cancel = new() { Text = "취소", AutoSize = true, Enabled = false };
    private readonly List<Button> actions = [];
    private readonly List<object> evidence = [];
    private readonly Guid runId = Guid.NewGuid();
    private Credential? credential;
    private CancellationTokenSource? running;
    private string? transferHash;
    private string nativeState = "not_run";

    public ProbeWindow()
    {
        if (cloud.Enabled) { SetupCloudUi(); return; }
        Text = "HoleInOne · Steam 로그인 호환성 검증";
        ClientSize = new Size(740, 720); MinimumSize = new Size(720, 740);
        Font = new Font("Malgun Gothic", 10);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(20) };
        Controls.Add(panel);
        panel.Controls.Add(new Label { Text = "1. 앱 내부 신규 인증", AutoSize = true });
        panel.Controls.Add(new Label { Text = "Steam 비밀번호·Guard 코드는 인증 중에만 사용합니다. 서버 업로드는 아직 하지 않습니다.", AutoSize = true });
        var login = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(680, 0) };
        login.Controls.Add(username); login.Controls.Add(password);
        login.Controls.Add(ActionButton("비밀번호 로그인", () => Authenticate(false)));
        login.Controls.Add(ActionButton("QR 로그인", () => Authenticate(true)));
        panel.Controls.Add(login); panel.Controls.Add(qr);
        var transfer = new FlowLayoutPanel { AutoSize = true };
        transfer.Controls.Add(ActionButton("인증을 암호화해 저장", Export));
        transfer.Controls.Add(ActionButton("암호화된 인증 열기", Import));
        panel.Controls.Add(transfer);
        panel.Controls.Add(new Label { Text = "2. 깨끗한 테스트 환경에서 Steam 자동 로그인", AutoSize = true, Margin = new Padding(0, 16, 0, 6) });
        panel.Controls.Add(new Label { Text = "별도 Windows 사용자/VM과 로그인 이력 없는 Steam 폴더만 사용하세요.\n기존 인증 파일이 있거나 Steam이 실행 중이면 복원을 거부합니다.\n시험 후 Steam과 테스트 환경의 로그인 자료는 남습니다. 테스트 환경을 폐기하세요.", AutoSize = true });
        panel.Controls.Add(ActionButton("Steam.exe 선택 후 자동 로그인 시험", TestNative));
        panel.Controls.Add(state);
        var bottom = new FlowLayoutPanel { AutoSize = true };
        cancel.Click += (_, _) => running?.Cancel(); bottom.Controls.Add(cancel);
        bottom.Controls.Add(ActionButton("민감정보 없는 결과 저장", SaveReport)); panel.Controls.Add(bottom);
        FormClosing += (_, e) => { if (running != null) { running.Cancel(); e.Cancel = true; state.Text = "취소 처리 후 창을 다시 닫아 주세요."; } };
        FormClosed += (_, _) => { credential = null; password.Clear(); qr.Image?.Dispose(); };
    }

    private Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += async (_, _) => {
            if (running != null) return;
            running = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            foreach (var item in actions) item.Enabled = false;
            cancel.Enabled = true;
            try { await action(); }
            catch (Exception error)
            {
                var code = error switch {
                    ProbeFailure failure => failure.Message,
                    OperationCanceledException => "CANCELED_OR_TIMED_OUT",
                    TimeoutException => "STEAM_TIMEOUT",
                    CryptographicException => "TRANSFER_PASSWORD_OR_FILE_INVALID",
                    InvalidDataException => "INVALID_PROBE_DATA",
                    UnauthorizedAccessException => "FILE_ACCESS_DENIED",
                    _ => "PROBE_FAILED"
                };
                // Never show raw Steam/network/JSON exceptions: they may contain credentials.
                state.Text = cloud.Enabled ? "Result: " + code + ". No credentials are included in the report." : Explain(code);
                evidence.Add(new { operation = text, result = code, at = DateTimeOffset.UtcNow });
            }
            finally {
                password.Clear(); qr.Image?.Dispose(); qr.Image = null;
                running.Dispose(); running = null; cancel.Enabled = false;
                foreach (var item in actions) item.Enabled = true;
                if (cloud.Enabled) TryWriteCloudReport();
            }
        };
        actions.Add(button); return button;
    }

    private async Task Authenticate(bool useQr)
    {
        var loginName = username.Text.Trim(); var secret = password.Text;
        password.Clear();
        if (!useQr && (loginName.Length == 0 || secret.Length == 0)) throw new ProbeFailure("CREDENTIALS_REQUIRED");
        state.Text = useQr ? "Steam 모바일 앱으로 QR을 승인하세요." : "Steam 인증을 진행합니다. 필요하면 Guard 코드를 입력하세요.";
        var ct = running!.Token;
        await using var auth = new SteamAuthentication();
        credential = await auth.AuthenticateAsync(useQr, loginName, secret, bytes => {
            Ui(() => { if (ct.IsCancellationRequested) return; using var stream = new MemoryStream(bytes);
                using var source = Image.FromStream(stream); var old = qr.Image; qr.Image = new Bitmap(source); old?.Dispose(); });
        }, prompt => RequestCode(prompt, ct), ct);
        transferHash = null; nativeState = "not_run";
        state.Text = "Steam 인증 성공: " + credential.AccountName + " / " + credential.SteamId + ". 클라우드 자동 로그인은 아직 미검증입니다.";
        evidence.Add(new { operation = useQr ? "qr_auth" : "password_auth", result = "steamkit_verified", at = DateTimeOffset.UtcNow });
    }

    private Task<string> RequestCode(GuardPrompt prompt, CancellationToken ct)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(() => {
            if (ct.IsCancellationRequested) { result.TrySetCanceled(ct); return; }
            var title = (prompt.Incorrect ? "이전 코드가 올바르지 않습니다. " : "") +
                (prompt.Email ? "Steam Guard 이메일 코드" : "Steam Guard 앱 코드");
            var value = Input(title, true, ct);
            if (value == null) result.TrySetCanceled(); else result.TrySetResult(value.Trim());
        });
        return result.Task.WaitAsync(ct);
    }

    private async Task Export()
    {
        var value = credential ?? throw new ProbeFailure("AUTHENTICATE_FIRST");
        var key = Input("다른 테스트 환경에서 열 때 사용할 16자 이상의 암호", true, running!.Token);
        if (key == null) return;
        var confirmation = Input("같은 암호를 다시 입력하세요", true, running.Token);
        if (confirmation == null) return;
        if (key != confirmation) throw new ProbeFailure("TRANSFER_PASSWORD_MISMATCH");
        if (key.Length < 16) throw new ProbeFailure("TRANSFER_PASSWORD_TOO_SHORT");
        using var save = new SaveFileDialog { Filter = "암호화된 테스트 인증|*.hioauth", FileName = "steam-test.hioauth", OverwritePrompt = true };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        var bytes = await Task.Run(() => EncryptedTransfer.Seal(value, key, DateTimeOffset.UtcNow), running.Token);
        await File.WriteAllBytesAsync(save.FileName, bytes, running.Token);
        transferHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        state.Text = "암호화된 테스트 인증을 저장했습니다. 파일과 암호를 채팅·저장소에 올리지 마세요. 이 검증 파일은 7일 이내에 사용하세요.";
        evidence.Add(new { operation = "encrypted_export", result = "saved", transferSha256 = transferHash, at = DateTimeOffset.UtcNow });
    }

    private async Task Import()
    {
        using var open = new OpenFileDialog { Filter = "암호화된 테스트 인증|*.hioauth" };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(open.FileName).Length > EncryptedTransfer.MaxBytes) throw new ProbeFailure("INVALID_TRANSFER");
        var key = Input("테스트 인증 파일의 암호", true, running!.Token); if (key == null) return;
        var bytes = await File.ReadAllBytesAsync(open.FileName, running.Token);
        var imported = await Task.Run(() => EncryptedTransfer.Open(bytes, key, DateTimeOffset.UtcNow), running.Token);
        credential = imported; nativeState = "not_run";
        transferHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        state.Text = "암호화된 인증을 열었습니다. Steam 자동 로그인 시험 시 서버에서 계정을 다시 확인합니다.";
        evidence.Add(new { operation = "encrypted_import", result = "opened", transferSha256 = transferHash, at = DateTimeOffset.UtcNow });
    }

    private async Task TestNative()
    {
        using var open = new OpenFileDialog { Filter = "Steam 실행 파일|steam.exe" };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(this, "이 시험은 선택한 Steam 폴더와 현재 Windows 사용자에게 로그인 자료를 생성하고 Steam을 실행합니다.\n\n로그인 이력이 없는 별도 테스트 환경입니까? 평소 사용하는 Steam에는 실행하지 마세요.",
            "테스트 환경 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RunNative(open.FileName);
    }

    private async Task RunNative(string steamExe)
    {
        var value = credential ?? throw new ProbeFailure("AUTHENTICATE_FIRST");
        nativeState = "running";
        try {
            await NativeProbe.RunAsync(value, steamExe, message => Ui(() => state.Text = cloud.Enabled ? "Validating account and starting Steam. Do not log in manually. Please wait up to 180 seconds." : message), running!.Token);
            nativeState = "expected_account_process_detected";
            state.Text = cloud.Enabled ? "Expected Steam account detected. Click Open Steam window, check the account and online state, then return here to confirm." : "예상 Steam 계정과 실행 프로세스를 확인했습니다. Steam 화면도 확인한 뒤 결과를 저장하세요. 게임 실행·GameLift 검증과는 별개입니다.";
            evidence.Add(new { operation = "native_login", result = nativeState, candidate = "dpapi-utf8-v1", transferSha256 = transferHash,
                actualAccountMatches = true, at = DateTimeOffset.UtcNow });
        }
        catch { nativeState = "not_confirmed"; throw; }
    }

    private Task SaveReport()
    {
        using var save = new SaveFileDialog { Filter = "검증 결과|*.json", FileName = "steam-probe-result.json" };
        if (save.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
        File.WriteAllText(save.FileName, ReportJson());
        state.Text = "결과 JSON을 저장했습니다. 계정 이름·SteamID·비밀번호·토큰·파일 경로는 포함하지 않습니다.";
        return Task.CompletedTask;
    }

    private string ReportJson() => JsonSerializer.Serialize(new { schema = 1, runId, probeVersion = "0.2.0-gamelift-gate",
        platform = Environment.OSVersion.VersionString, mode = cloud.Enabled ? "gamelift-isolated-test" : "local",
        nativeState, operatorConfirmedOnline, productionReady = false, cloudVerified = false, observations = evidence },
        new JsonSerializerOptions { WriteIndented = true });

    private string? Input(string title, bool sensitive, CancellationToken ct)
    {
        using var dialog = new Form { Text = title, ClientSize = new Size(560, 135), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, Font = Font };
        var label = new Label { Text = title, AutoSize = true, Location = new Point(12, 12), MaximumSize = new Size(535, 0) };
        var input = new TextBox { UseSystemPasswordChar = sensitive, Location = new Point(12, 46), Width = 530, MaxLength = 1024 };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Location = new Point(358, 88), Width = 88 };
        var close = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(454, 88), Width = 88 };
        dialog.Controls.AddRange([label, input, ok, close]); dialog.AcceptButton = ok; dialog.CancelButton = close;
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) => { if (ct.IsCancellationRequested) dialog.Close(); }; timer.Start();
        var answer = dialog.ShowDialog(this) == DialogResult.OK && !ct.IsCancellationRequested ? input.Text : null;
        input.Clear(); return answer;
    }

    private void Ui(Action action)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) BeginInvoke(action); else action();
    }

    private static string Explain(string code) => code switch {
        "CLEAN_TEST_PROFILE_REQUIRED" => "기존 Steam 설정이 있어 변경하지 않았습니다. 새 테스트 Windows 사용자와 깨끗한 Steam 폴더가 필요합니다.",
        "STEAM_ALREADY_RUNNING" => "Steam이 실행 중이어서 변경하지 않았습니다. 별도 테스트 환경에서 다시 시도하세요.",
        "NATIVE_LOGIN_NOT_CONFIRMED" => "180초 안에 자동 로그인을 확인하지 못했습니다. 수동 로그인하지 말고 이 결과와 화면 상태를 알려주세요.",
        "AUTHENTICATE_FIRST" => "먼저 신규 인증하거나 암호화된 테스트 인증을 여세요.",
        "TRANSFER_PASSWORD_TOO_SHORT" => "테스트 인증 파일의 암호는 16자 이상이어야 합니다.",
        "TRANSFER_PASSWORD_MISMATCH" => "두 번 입력한 암호가 다릅니다.",
        _ => "검증 결과: " + code + ". 결과 JSON에는 민감정보가 포함되지 않습니다."
    };
}
