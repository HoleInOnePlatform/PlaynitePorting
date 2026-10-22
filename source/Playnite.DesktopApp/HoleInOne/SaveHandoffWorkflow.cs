using Microsoft.Win32;
using Playnite.HoleInOne;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Playnite.DesktopApp.HoleInOne
{
    // Owns export polling, verified restore and receipt scheduling. UI supplies progress and lifetime callbacks.
    internal static class SaveHandoffWorkflow
    {
        public static async Task ApplyAsync(Game game, string key, CloudSession session, BackendConnection backend,
            ExportReceipts receipts, Func<Guid, Game> readGame, Action<string> progress, Action requireLive,
            CancellationToken ct, PerformanceTrace handoffTiming)
        {
                        progress("세이브 전환을 요청하는 중입니다. 연결이 잠시 끊기면 자동으로 다시 확인합니다.");
                        var status = await handoffTiming.RunAsync("request_export", () => backend.StartExportAsync(session.SessionId, ct));
                        if (!System.Text.RegularExpressions.Regex.IsMatch(status.RequestId ?? "", "^[a-zA-Z0-9_-]{1,128}$"))
                            throw new InvalidDataException("잘못된 세이브 전환 식별자임.");
                        var requestId = status.RequestId;
                        LogManager.GetLogger().Info("[HIO-PERF] link trace=" + handoffTiming.Id + " export=" + requestId);
                        using (var exportWait = handoffTiming.Begin("server_export_wait"))
                        {
                            while (true)
                            {
                                ct.ThrowIfCancellationRequested();
                                requireLive();
                                if (status.GameId != key || status.RequestId != requestId) throw new InvalidDataException("다른 게임 또는 전환 요청의 응답임.");
                                if (status.Status == "ready" || status.Status == "no_save") break;
                                if (status.Status == "failed") throw new InvalidOperationException(status.FailureMessage());
                                if (status.Status != "exporting" && status.Status != "preparing") throw new InvalidDataException("잘못된 세이브 전환 상태임.");
                                progress(status.Status == "preparing" ? "받은 파일에서 세이브 데이터를 준비하는 중입니다." : "클라우드 게임을 종료하고 저장된 데이터를 가져오는 중입니다.");
                                await Task.Delay(1000, ct);
                                status = await backend.GetExportAsync(session.SessionId, ct);
                            }
                            exportWait.Complete();
                        }
                        if (status.Status == "ready")
                        {
                            // Save destinations and Steam identity are irrelevant when there is no save.
                            var roots = LocalRoots(game.InstallDirectory);
                            SteamLinkContext.RequireLocalAccount();
                            var account = LocalSteamAccount.ActiveAccountId();
                            var accountId = account == 0 ? null : account.ToString(CultureInfo.InvariantCulture);
                            var steamId = LocalSteamAccount.SteamId(account);
                            var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                            if (accountId != null && !string.IsNullOrEmpty(steamPath))
                            {
                                roots["steamUserData"] = Path.Combine(steamPath, "userdata", accountId);
                                roots["steamUserDataRoot"] = Path.Combine(steamPath, "userdata");
                            }
                            var work = Path.Combine(PlaynitePaths.ConfigRootPath, "HoleInOne", "SaveHandoffs", session.SessionId, Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(work);
                            var archive = Path.Combine(work, "saves.zip");
                            progress("세이브 데이터를 다운로드하는 중입니다.");
                            await handoffTiming.RunAsync("download_and_verify_archive", () => backend.DownloadExportAsync(session.SessionId, status, archive, ct));
                            requireLive();
                            game = readGame(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                            if (game.IsRunning || !game.IsInstalled || game.IsInstalling ||
                                !string.Equals(game.InstallDirectory, roots["install"], StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("로컬 게임 상태 또는 설치 위치가 변경됨. 전환 중단.");
                            progress("세이브를 검증하고 저장된 진행 상황을 준비하는 중입니다.");
                            var restore = await handoffTiming.RunAsync("validate_and_stage_saves", () => Task.Run(
                                () => ExportSaveInstaller.Prepare(archive, key, roots, steamId, accountId, Path.Combine(work, "restore"), ct),
                                ct));
                            requireLive();
                            game = readGame(game.Id) ?? throw new InvalidOperationException("라이브러리에서 게임이 제거됨.");
                            if (game.IsRunning || game.IsLaunching || !game.IsInstalled || game.IsInstalling ||
                                !string.Equals(game.InstallDirectory, roots["install"], StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("로컬 게임 상태 또는 설치 위치가 변경됨. 전환 중단.");
                            if (steamId != null && LocalSteamAccount.RequireSteamId() != steamId)
                                throw new InvalidOperationException("Steam 계정이 변경되어 전환을 중단함.");
                            progress("기존 세이브를 백업하고 클라우드 세이브를 적용하는 중입니다.");
                            await handoffTiming.RunAsync("backup_and_commit_saves", () => Task.Run(() => restore.Commit(ct), ct));
                            // Persist cleanup, then send independently of local launch and webview cancellation.
                            handoffTiming.Run("persist_receipt", () => receipts.Enqueue(session.SessionId, requestId, status.Sha256));
                            // The restored files and rollback backup are independent of the download.
                            // Retain recovery material; remove only this successfully applied temporary archive.
                            try { File.Delete(archive); }
                            catch (IOException) { LogManager.GetLogger().Warn("Applied save archive cleanup deferred."); }
                            catch (UnauthorizedAccessException) { LogManager.GetLogger().Warn("Applied save archive cleanup deferred."); }
                        }
        }

        private static Dictionary<string, string> LocalRoots(string installDirectory)
        {
            if (string.IsNullOrWhiteSpace(installDirectory) || !Path.IsPathRooted(installDirectory) || !Directory.Exists(installDirectory))
                throw new InvalidDataException("설치된 게임 경로를 확인하지 못함.");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["roaming"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ["local"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ["localLow"] = Path.Combine(profile, "AppData", "LocalLow"),
                ["documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                ["savedGames"] = Path.Combine(profile, "Saved Games"),
                ["profile"] = profile, ["install"] = installDirectory
            };
        }
    }
}
