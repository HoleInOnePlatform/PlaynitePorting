using Playnite.HoleInOne;

static class LocalDownloadChecks
{
    public static void Run(Action<bool,string> check)
    {
        var signedIn = false;
        var sameAccount = true;
        var installing = false;
        var installed = false;
        var installRequests = 0;
        var modalErrors = 0;
        void RequireAccount()
        {
            if (!signedIn) throw new InvalidOperationException("Steam에 로그인한 뒤 다시 시도해 주세요.");
            if (!sameAccount) throw new InvalidOperationException("다른 PC Steam 계정입니다.");
        }
        void InstallThroughEditor()
        {
            installRequests++;
            // GamesEditor.InstallGame handles this error internally; the coordinator cannot catch it.
            try { RequireAccount(); installing = true; }
            catch (InvalidOperationException) { modalErrors++; }
        }
        var download = new LocalDownloadSession(() => installed, () => installing, RequireAccount, InstallThroughEditor);
        download.Request();
        check(installRequests == 0 && modalErrors == 0 && download.CanRetry,
            "PC Steam absent defers install before the editor's modal error, with retry available");
        check(download.Message.Contains("Steam"), "PC login instructions are visible while cloud playback continues");
        signedIn = true; sameAccount = false;
        download.Refresh();
        check(installRequests == 0 && download.CanRetry, "different PC Steam account remains blocked");
        sameAccount = true;
        download.Refresh();
        check(installRequests == 1 && installing && !download.CanRetry, "same-account login automatically resumes installation in the existing session");
        check(download.Message == null, "normal download has no notification message");
        download.Refresh();
        download.Request();
        check(installRequests == 1, "duplicate retry cannot issue a second install");
        installing = false; download.Refresh();
        check(download.CanRetry && !download.Complete, "cancelled or failed download can be retried");
        download.Refresh();
        check(installRequests == 1, "cancelled installation is not automatically restarted");
        signedIn = false; download.Request();
        check(installRequests == 1 && download.CanRetry, "each retry revalidates PC login");
        signedIn = true; download.Request();
        installed = true; installing = false; download.Refresh();
        check(download.Complete && !download.CanRetry, "only completed installation permits handoff");
        download.Request();
        check(installRequests == 2, "completed installation cannot be requested again");
    }
}
