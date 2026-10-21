using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace Playnite.HoleInOne
{
    public sealed class ExportSaveManifest
    {
        public int Version { get; set; }
        public string GameId { get; set; }
        public List<ExportSaveFile> Files { get; set; }
    }
    public sealed class ExportSaveFile
    {
        public string Entry { get; set; }
        public string Root { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
    }

    // Verify and stage the entire bundle before touching live saves. Keep rollback copies on disk.
    public static class ExportSaveInstaller
    {
        private static bool IsArchiveLink(ZipArchiveEntry entry)
        {
            // The net462 reference assembly predates this API; supported Windows runtimes expose it.
            // Fail closed on an older runtime rather than silently accepting symlinks.
            var property = typeof(ZipArchiveEntry).GetProperty("ExternalAttributes");
            if (property == null) throw new InvalidDataException("안전한 세이브 전환을 위해 .NET Framework 4.8이 필요합니다.");
            return (((int)property.GetValue(entry, null) >> 16) & 0xF000) == 0xA000;
        }

        private sealed class Change
        {
            public string Target;
            public string Staged;
            public string Backup;
            public bool Existed;
            public string OriginalHash;
        }

        public sealed class PreparedRestore
        {
            private readonly Action<CancellationToken> commit;
            private int committed;
            internal PreparedRestore(Action<CancellationToken> commit) { this.commit = commit; }
            public void Commit(CancellationToken ct)
            {
                if (Interlocked.Exchange(ref committed, 1) != 0) throw new InvalidOperationException("이미 사용된 복원 준비입니다.");
                commit(ct);
            }
        }

        public static void Apply(string archivePath, string gameId, IDictionary<string, string> roots,
            string steamId64, string steamAccountId, string workingDirectory, CancellationToken ct)
            => Prepare(archivePath, gameId, roots, steamId64, steamAccountId, workingDirectory, ct).Commit(ct);

        // Writes only staging files. Live saves are touched only by Commit after Steam succeeds.
        public static PreparedRestore Prepare(string archivePath, string gameId, IDictionary<string, string> roots,
            string steamId64, string steamAccountId, string workingDirectory, CancellationToken ct)
        {
            if (Directory.Exists(workingDirectory)) throw new InvalidDataException("이미 사용된 세이브 복원 작업 폴더임.");
            Directory.CreateDirectory(workingDirectory);
            var changes = new List<Change>();
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (archive.Entries.Count > 50001) throw new InvalidDataException("세이브 파일 수가 너무 많음.");
                var metadata = archive.GetEntry("manifest.json");
                if (metadata == null || metadata.Length > 16 * 1024 * 1024) throw new InvalidDataException("세이브 목록이 없거나 너무 큼.");
                ExportSaveManifest manifest;
                using (var reader = new StreamReader(metadata.Open())) manifest = JsonConvert.DeserializeObject<ExportSaveManifest>(reader.ReadToEnd());
                if (manifest == null || manifest.Version != 1 || manifest.GameId != gameId || manifest.Files == null ||
                    manifest.Files.Count == 0 || manifest.Files.Count > 50000 || manifest.Files.Count + 1 != archive.Entries.Count)
                    throw new InvalidDataException("세이브 목록이 현재 게임과 일치하지 않음.");
                var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
                var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (var file in manifest.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (file == null || !Regex.IsMatch(file.Entry ?? "", @"^files/[0-9]{6}$") || !entries.Add(file.Entry) ||
                        file.Size < 0 || !Regex.IsMatch(file.Sha256 ?? "", "^[a-f0-9]{64}$"))
                        throw new InvalidDataException("잘못된 세이브 파일 정보임.");
                    total = checked(total + file.Size);
                    if (total > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("세이브 크기 제한을 초과함.");
                    var entry = archive.GetEntry(file.Entry);
                    if (entry == null || entry.Length != file.Size || IsArchiveLink(entry))
                        throw new InvalidDataException("세이브 파일 크기가 다르거나 링크가 포함됨.");
                    if (!roots.TryGetValue(file.Root ?? "", out var root)) throw new InvalidDataException("지원되지 않는 로컬 저장 위치임.");
                    var relative = file.Path ?? "";
                    relative = MapAccount(relative, "{steamId64}", steamId64);
                    relative = MapAccount(relative, "{steamAccountId}", steamAccountId);
                    var target = ResolvePath(root, relative);
                    if (!targets.Add(target)) throw new InvalidDataException("여러 세이브가 같은 로컬 파일을 가리킴.");
                    RejectLinks(target);
                    var staged = System.IO.Path.Combine(workingDirectory, "staged", changes.Count.ToString("D6"));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(staged));
                    using (var input = entry.Open())
                    using (var output = File.Open(staged, FileMode.CreateNew, FileAccess.Write))
                    using (var hash = SHA256.Create())
                    {
                        var buffer = new byte[65536];
                        long written = 0;
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            written += read;
                            if (written > file.Size) throw new InvalidDataException("세이브 압축 해제 크기가 다름.");
                            hash.TransformBlock(buffer, 0, read, buffer, 0);
                            output.Write(buffer, 0, read);
                        }
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        if (written != file.Size || Hex(hash.Hash) != file.Sha256) throw new InvalidDataException("세이브 파일 검증에 실패함.");
                    }
                    changes.Add(new Change { Target = target, Staged = staged, Existed = File.Exists(target),
                        OriginalHash = File.Exists(target) ? HashFile(target) : null,
                        Backup = System.IO.Path.Combine(workingDirectory, "backup", changes.Count.ToString("D6")) });
                }
                if (archive.Entries.Any(e => !entries.Contains(e.FullName)) || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
                    throw new InvalidDataException("세이브 압축에 중복 또는 예상하지 않은 항목이 포함됨.");
            }
            return new PreparedRestore(token => Commit(changes, workingDirectory, gameId, token));
        }

        private static void Commit(List<Change> changes, string workingDirectory, string gameId, CancellationToken ct)
        {
            var committed = new List<Change>();
            Directory.CreateDirectory(System.IO.Path.Combine(workingDirectory, "backup"));
            // Record where each backup belongs so failures can be recovered after an app restart.
            File.WriteAllText(System.IO.Path.Combine(workingDirectory, "restore-map.json"), JsonConvert.SerializeObject(changes, Formatting.Indented));
            foreach (var change in changes)
            {
                ct.ThrowIfCancellationRequested();
                RejectLinks(change.Target);
                if (File.Exists(change.Target) != change.Existed ||
                    (change.Existed && HashFile(change.Target) != change.OriginalHash))
                    throw new IOException("복원 준비 중 로컬 세이브가 변경됨.");
                if (change.Existed)
                {
                    File.Copy(change.Target, change.Backup, false);
                    if (HashFile(change.Backup) != change.OriginalHash) throw new IOException("백업 중 로컬 세이브가 변경됨.");
                }
            }
            try
            {
                foreach (var change in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    RejectLinks(change.Target);
                    if (File.Exists(change.Target) != change.Existed) throw new IOException("복원 도중 로컬 세이브가 변경됨.");
                    if (change.Existed && HashFile(change.Target) != HashFile(change.Backup)) throw new IOException("복원 도중 로컬 세이브가 변경됨.");
                    ReplaceFrom(change.Staged, change.Target);
                    committed.Add(change);
                }
                ct.ThrowIfCancellationRequested();
                File.WriteAllText(System.IO.Path.Combine(workingDirectory, "applied"), gameId);
            }
            catch (Exception error)
            {
                var rollbackErrors = new List<Exception>();
                foreach (var change in committed.AsEnumerable().Reverse())
                {
                    try
                    {
                        RejectLinks(change.Target);
                        if (change.Existed) ReplaceFrom(change.Backup, change.Target);
                        else File.Delete(change.Target);
                    }
                    catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
                }
                if (rollbackErrors.Count != 0) throw new AggregateException("세이브 복구에 실패함. 작업 폴더의 backup과 restore-map.json으로 복구가 필요함.", new[] { error }.Concat(rollbackErrors));
                throw;
            }
        }

        private static string MapAccount(string path, string token, string account)
        {
            if (!path.Contains(token)) return path;
            if (!Regex.IsMatch(account ?? "", "^[0-9]+$")) throw new InvalidDataException("현재 Steam 계정을 확인하지 못함. Steam 로그인 필요.");
            return path.Replace(token, account);
        }

        public static string ResolvePath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(root) || !System.IO.Path.IsPathRooted(root) || string.IsNullOrWhiteSpace(relative) ||
                relative.Contains('\\') || relative.Split('/').Any(p => p.Length == 0 || p == "." || p == ".." ||
                    p.EndsWith(" ") || p.EndsWith(".") || p.Any(c => c < 32 || ":*?\"<>|{}".Contains(c)) ||
                    Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)))
                throw new InvalidDataException("잘못된 세이브 저장 경로임.");
            var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var result = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("세이브 저장 경로가 허용 범위를 벗어남.");
            return result;
        }

        private static void RejectLinks(string path)
        {
            for (var current = path; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("세이브 저장 경로에 링크가 포함됨.");
        }

        private static void ReplaceFrom(string source, string target)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".hio-part";
            try
            {
                File.Copy(source, temporary, false);
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static string HashFile(string path)
        {
            using (var input = File.OpenRead(path))
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(input));
        }
        private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
    }
}
