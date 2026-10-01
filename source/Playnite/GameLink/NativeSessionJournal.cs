using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Playnite.GameLink
{
    internal sealed class NativeSessionJournal : IDisposable
    {
        private readonly string path; private readonly FileStream ownership; private readonly object sync = new object();
        private JObject data;
        public string RequestId => (string)data["requestId"];
        public string SessionId { get => (string)data["sessionId"]; set { data["sessionId"] = value; Save(); } }
        public long Cursor { get => (long?)data["cursor"] ?? 0; set { data["cursor"] = value; Save(); } }
        public bool Closed { get => (bool?)data["closed"] == true; set { data["closed"] = value; Save(); } }
        public string CredentialFingerprint { get; }
        public static string Credentials() => SaveRestoreService.Hash(Encoding.UTF8.GetBytes(NativeBackendConfiguration.Load()?.Token ?? ""));
        public NativeSessionJournal(Uri origin, Guid localGame)
        {
            CredentialFingerprint = Credentials();
            var key = SaveRestoreService.Hash(Encoding.UTF8.GetBytes(origin.AbsoluteUri + "|" + CredentialFingerprint + "|" + localGame));
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoleInOne", "native-sessions");
            Directory.CreateDirectory(dir);
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Native session directory is a link.");
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(dir, security);
            path = Path.Combine(dir, key + ".json");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Native session journal is a link.");
            ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            data = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            if (data == null || (bool?)data["closed"] == true) Reset();
        }
        public void Reset()
        { data = new JObject { ["version"] = 2, ["requestId"] = Guid.NewGuid().ToString("N"), ["cursor"] = 0, ["closed"] = false }; Save(); }
        private void Save()
        {
            lock (sync)
            {
                var temp = path + ".tmp"; var bytes = Encoding.UTF8.GetBytes(data.ToString(Formatting.None));
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
        }
        public void Dispose() => ownership.Dispose();
    }
}
