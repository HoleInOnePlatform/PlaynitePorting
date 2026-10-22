using System;

namespace Playnite.HoleInOne
{
    // Local installation can wait for PC Steam while the independently authenticated cloud game runs.
    public sealed class LocalDownloadSession
    {
        private readonly Func<bool> installed, installing;
        private readonly Action requireLocalAccount, install;
        public bool Complete => installed() && !installing();
        public bool CanRetry => !Complete && !installing();
        public string Message { get; private set; }
        public bool WaitingForAccount { get; private set; }

        public LocalDownloadSession(Func<bool> installed, Func<bool> installing, Action requireLocalAccount, Action install)
        {
            this.installed = installed;
            this.installing = installing;
            this.requireLocalAccount = requireLocalAccount;
            this.install = install;
        }

        public void Request()
        {
            if (!CanRetry) return;
            try { requireLocalAccount(); }
            catch (InvalidOperationException error)
            {
                WaitingForAccount = true;
                Message = "로컬 다운로드를 시작할 수 없습니다. " + error.Message +
                    "\n클라우드 플레이는 계속할 수 있습니다. 같은 계정으로 PC Steam에 로그인하면 다운로드를 시작합니다.";
                return;
            }
            WaitingForAccount = false;
            install();
            Message = null;
            Refresh();
        }

        public void Refresh()
        {
            if (Complete || installing())
            {
                WaitingForAccount = false;
                Message = null;
            }
            else if (WaitingForAccount)
            {
                // Only an authentication wait may resume automatically. Never repeatedly invoke
                // an installer that failed or was cancelled by the user.
                try { requireLocalAccount(); }
                catch (InvalidOperationException) { return; }
                Request();
            }
            else
                Message = "로컬 다운로드가 시작되지 않았거나 중단되었습니다. PC Steam에서 다운로드 상태를 확인해 주세요. 클라우드 플레이는 계속할 수 있습니다.";
        }
    }
}
