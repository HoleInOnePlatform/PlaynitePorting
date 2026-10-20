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
                Message = error.Message + "\n클라우드 플레이는 계속할 수 있습니다. PC Steam 로그인 후 '다운로드 다시 시도'를 눌러주세요.";
                return;
            }
            install();
            Message = null;
            Refresh();
        }

        public void Refresh()
        {
            if (Complete) Message = "PC 다운로드가 완료되었습니다.";
            else if (installing()) Message = "PC에 다운로드 중입니다. 클라우드에서 계속 플레이할 수 있습니다.";
            else if (Message == null || Message.StartsWith("PC에 다운로드 중", StringComparison.Ordinal))
                Message = "PC 다운로드가 시작되지 않았거나 중단되었습니다. Steam에서 상태를 확인하고 다시 시도해 주세요.";
        }
    }
}
