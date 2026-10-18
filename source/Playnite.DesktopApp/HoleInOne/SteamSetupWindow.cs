using Playnite.HoleInOne;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Playnite.DesktopApp.HoleInOne
{
    public sealed class SteamSetupWindow : Playnite.Controls.WindowBase
    {
        private readonly CancellationTokenSource closed = new CancellationTokenSource();
        private CancellationTokenSource operation;
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,12) };
        private readonly TextBlock preparation = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        private readonly Image picture = new Image { Width=220, Height=220, Visibility=Visibility.Collapsed };
        private readonly TextBox username = new TextBox { MinHeight=32, MaxLength=64 };
        private readonly PasswordBox password = new PasswordBox { MinHeight=32, MaxLength=256 };
        private readonly Button qr = new Button { Content="QR로 Steam 연결", MinHeight=36, Margin=new Thickness(0,8,0,6) };
        private readonly Button login = new Button { Content="아이디·비밀번호로 연결", MinHeight=36, Margin=new Thickness(0,8,0,6) };
        private readonly Button release = new Button { Content="준비 세션 종료", Margin=new Thickness(0,6,0,6) };
        private readonly Button unlink = new Button { Content="Steam 연결 해제", Margin=new Thickness(0,6,0,6) };
        private bool busy;
        public SteamSetupWindow()
        {
            Title="HoleInOne · Steam 연결"; Width=480; MaxHeight=850; SizeToContent=SizeToContent.Height;
            WindowStartupLocation=WindowStartupLocation.CenterOwner; ShowMinimizeButton=false; ShowMaximizeButton=false;
            SetResourceReference(StyleProperty,"StandardWindowStyle");
            Resources.Add(typeof(TextBlock),Application.Current.FindResource("BaseTextBlockStyle"));
            var panel=new StackPanel { Margin=new Thickness(24) }; Content=new ScrollViewer { Content=panel, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            panel.Children.Add(new TextBlock { Text="Steam을 한 번 연결하세요", FontSize=24, FontWeight=FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text="보유 게임 조회, 클라우드 자동 로그인, 선택한 세이브 동기화에 같은 계정을 사용합니다. 앱 종료와 HoleInOne 로그아웃 후에도 Steam 연결은 유지됩니다.", TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,10,0,0) });
            panel.Children.Add(status); panel.Children.Add(preparation); panel.Children.Add(qr); panel.Children.Add(picture);
            panel.Children.Add(new TextBlock { Text="Steam 로그인 아이디", Margin=new Thickness(0,8,0,4) }); panel.Children.Add(username);
            panel.Children.Add(new TextBlock { Text="Steam 비밀번호", Margin=new Thickness(0,8,0,4) }); panel.Children.Add(password); panel.Children.Add(login);
            panel.Children.Add(release); panel.Children.Add(unlink);
            var cancel=new Button { Content="인증 취소", Margin=new Thickness(0,6,0,6) }; panel.Children.Add(cancel); cancel.Click+=(s,e)=>operation?.Cancel();
            var close=new Button { Content="닫고 라이브러리 사용", Margin=new Thickness(0,6,0,0) }; panel.Children.Add(close); close.Click+=(s,e)=>Close();
            qr.Click+=async(s,e)=>await ConnectAsync(true); login.Click+=async(s,e)=>await ConnectAsync(false);
            release.Click+=async(s,e)=>await ChangeAsync(async()=>{
                await CloudPreparation.ResetAsync();
                using(var backend=SteamLinkContext.Backend()) await backend.ReleaseSteamPreparationAsync(closed.Token);
            });
            unlink.Click+=async(s,e)=>await ChangeAsync(async()=>{
                using(var backend=SteamLinkContext.Backend()) await backend.DeleteSteamLinkAsync(closed.Token);
                SteamLinkContext.DeleteLocal();
                await CloudPreparation.ResetAsync();
            });
            Closed+=(s,e)=>{ closed.Cancel(); operation?.Cancel(); password.Clear(); picture.Source=null; };
            Loaded+=async(s,e)=>await RefreshAsync();
        }
        private void Controls(bool enabled)
        { qr.IsEnabled=login.IsEnabled=username.IsEnabled=password.IsEnabled=release.IsEnabled=unlink.IsEnabled=enabled; }
        private async Task RefreshAsync()
        {
            try
            {
                if(!SteamLinkContext.IsConfigured) { status.Text="HoleInOne 로그인과 HTTPS 연결을 지원하는 서버가 필요합니다."; Controls(false); return; }
                var link=await SteamLinkContext.RefreshAsync(closed.Token);
                status.Text=link.State=="unlinked" ? "미연결 · 연결을 취소해도 로컬 라이브러리는 사용할 수 있습니다." : link.State=="reauth_required" ? "재인증 필요 · 아래에서 다시 연결해 주세요." :
                    "연결됨 · Steam " + link.SteamId + (SteamLinkContext.HasLocalLogin ? "" : "\n클라우드 로그인은 유지됩니다. 이 PC의 라이브러리·세이브 연동에는 다시 연결이 필요합니다.");
                preparation.Text=link.SessionState==null ? "클라우드 준비 세션 없음" : "클라우드 상태: " + SessionText(link.SessionState);
                if(link.DeletionPending)status.Text+="\n서버에서 이전 인증 자료 삭제를 재시도하고 있습니다.";
            }
            catch(Exception error) { if(!closed.IsCancellationRequested) status.Text=Friendly(error); }
        }
        private async Task ConnectAsync(bool useQr)
        {
            if(busy)return;
            if(InstantPlayCoordinator.ActiveStreamCount!=0) { status.Text="클라우드 이용을 정상 종료한 뒤 계정을 변경해 주세요."; return; }
            if(!useQr && (string.IsNullOrWhiteSpace(username.Text)||password.Password.Length==0)) { status.Text="Steam 아이디와 비밀번호를 입력해 주세요."; return; }
            busy=true; Controls(false); operation=CancellationTokenSource.CreateLinkedTokenSource(closed.Token); operation.CancelAfter(TimeSpan.FromMinutes(5));
            status.Text="인증 중…";
            try
            {
                var current=await SteamLinkContext.RefreshAsync(operation.Token);
                if(current.SessionState!=null)throw new SteamLinkException("steam_session_in_use");
                using(var bridge=new SteamLinkBridge())
                {
                    var secret=useQr ? "" : password.Password; password.Clear();
                    await bridge.ConnectAsync(useQr,username.Text.Trim(),secret,bytes=>{
                        using(var input=new MemoryStream(bytes)) { var bitmap=new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption=BitmapCacheOption.OnLoad; bitmap.StreamSource=input; bitmap.EndInit(); bitmap.Freeze(); picture.Source=bitmap; picture.Visibility=Visibility.Visible; }
                    },GuardAsync,operation.Token);
                    secret=null;
                }
                await RefreshAsync();
                status.Text+="\n연결 정보를 저장했습니다. 다음 실행부터 클라우드 로그인에 재사용합니다.";
                CloudPreparation.Begin();
            }
            catch(OperationCanceledException) { if(!closed.IsCancellationRequested)status.Text="인증이 취소되었거나 시간이 만료되었습니다. 서버 저장 요청 이후 취소했다면 다시 열어 연결 상태를 확인해 주세요."; }
            catch(Exception error) { if(!closed.IsCancellationRequested)status.Text=Friendly(error); }
            finally { password.Clear(); picture.Source=null; picture.Visibility=Visibility.Collapsed; operation.Dispose(); operation=null; busy=false; Controls(true); }
        }
        private Task<string> GuardAsync(bool email,bool incorrect)
        {
            var dialog=new Playnite.Controls.WindowBase { Title="Steam Guard", Width=380, SizeToContent=SizeToContent.Height, Owner=this, WindowStartupLocation=WindowStartupLocation.CenterOwner, ShowMinimizeButton=false, ShowMaximizeButton=false };
            dialog.SetResourceReference(StyleProperty,"StandardWindowStyle");
            var panel=new StackPanel { Margin=new Thickness(24) }; dialog.Content=panel;
            panel.Children.Add(new TextBlock { Text=(incorrect?"코드가 올바르지 않습니다. 다시 입력해 주세요.\n":"")+(email?"이메일로 받은 코드":"Steam 모바일 앱의 Guard 코드"), TextWrapping=TextWrapping.Wrap });
            var code=new PasswordBox { MaxLength=64, Margin=new Thickness(0,12,0,12) }; panel.Children.Add(code);
            var ok=new Button { Content="확인", IsDefault=true }; panel.Children.Add(ok); ok.Click+=(s,e)=>dialog.DialogResult=true;
            string result;
            using(operation.Token.Register(()=>Dispatcher.BeginInvoke(new Action(()=>dialog.Close()))))
            { if(dialog.ShowDialog()!=true)throw new OperationCanceledException(); result=code.Password; code.Clear(); }
            return Task.FromResult(result);
        }
        private async Task ChangeAsync(Func<Task> action)
        {
            if(busy)return;
            if(InstantPlayCoordinator.ActiveStreamCount!=0) { status.Text="클라우드 이용을 정상 종료한 뒤 변경해 주세요."; return; }
            busy=true; Controls(false);
            try { await action(); await RefreshAsync(); }
            catch(Exception error) { if(!closed.IsCancellationRequested)status.Text=Friendly(error); }
            finally { busy=false; Controls(true); }
        }
        private static string SessionText(string state) => state=="playing"?"플레이 중":state=="ready"?"준비 완료":state=="closing"?"종료 중":state=="transferring"?"세이브 전환 중":state=="preparing"?"준비 중":"로그인 확인 필요";
        private static string Friendly(Exception error) => error is SteamLinkException || error is LoginRequiredException ? error.Message : "연결을 완료하지 못했습니다. 인터넷 연결과 서버 상태를 확인한 뒤 다시 시도해 주세요.";
    }
    public static class SteamSetup
    {
        public static async Task OnStartupAsync()
        {
            if(!LauncherSettings.Load().RequireLogin)return;
            try { if(SteamLinkContext.IsConfigured && (await SteamLinkContext.RefreshAsync(CancellationToken.None)).IsLinked) return; }
            catch(Exception) { }
            new SteamSetupWindow { Owner=Application.Current.MainWindow }.ShowDialog();
        }
    }
}
