using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Playnite.GameLink
{
    // Only this native adapter can decrypt the current Windows user's connection record.
    internal sealed class NativeBackendConfiguration
    {
        internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HoleInOne.NativeConnection.v1");
        internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HoleInOne", "backend-connection.bin");
        public Uri Address { get; }
        public string Token { get; }
        public NativeBackendConfiguration(string address, string token)
        {
            if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) ||
                !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) || uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new NativeBackendConfigurationException("백엔드 주소는 HTTPS 서버의 기본 주소여야 합니다. 로컬 개발 서버만 HTTP를 허용합니다.");
            if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.IndexOfAny(new[] { '\r', '\n', ' ', '\t' }) >= 0)
                throw new NativeBackendConfigurationException("백엔드에서 발급한 인증 토큰을 입력해 주세요.");
            Address = uri; Token = token;
        }
        internal static NativeBackendConfiguration Load(string path = null, bool readEnvironment = true)
        {
            if (readEnvironment)
            {
                var url = Environment.GetEnvironmentVariable("HIO_BACKEND_URL");
                var token = Environment.GetEnvironmentVariable("HIO_USER_TOKEN");
                if (!string.IsNullOrWhiteSpace(url) || !string.IsNullOrWhiteSpace(token))
                    return new NativeBackendConfiguration(url, token); // Never mix credentials from different origins.
            }
            path = path ?? DefaultPath;
            if (!File.Exists(path)) return null;
            try
            {
                RejectLinks(path);
                if (new FileInfo(path).Length > 32768) throw new InvalidDataException();
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                var value = JObject.Parse(Encoding.UTF8.GetString(bytes));
                if ((int?)value["version"] != 1) throw new InvalidDataException();
                return new NativeBackendConfiguration((string)value["address"], (string)value["token"]);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is CryptographicException || e is Newtonsoft.Json.JsonException)
            { throw new NativeBackendConfigurationException("저장된 백엔드 연결 설정을 읽을 수 없습니다. 연결 설정을 다시 저장해 주세요."); }
        }
        internal void Save(string path = null)
        {
            path = path ?? DefaultPath;
            var dir = Path.GetDirectoryName(path);
            RejectLinks(path); Directory.CreateDirectory(dir); RejectLinks(path);
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(dir, acl);
            var value = new JObject { ["version"] = 1, ["address"] = Address.AbsoluteUri, ["token"] = Token };
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.ToString(Newtonsoft.Json.Formatting.None)), Entropy, DataProtectionScope.CurrentUser);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(encrypted, 0, encrypted.Length); f.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static void RejectLinks(string path)
        {
            for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Connection configuration cannot use linked directories.");
        }
    }
    internal sealed class NativeBackendConfigurationException : Exception
    { public NativeBackendConfigurationException(string message) : base(message) { } }
}
