using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Playnite.HoleInOne
{
    public sealed class ServiceConfiguration
    {
        public string BackendUrl { get; set; }

        public static ServiceConfiguration Load(string path, bool required)
        {
            if (!File.Exists(path))
            {
                if (required) throw new InvalidDataException("서비스 주소 파일이 없습니다. 홀인원을 다시 설치해 주세요.");
                return null;
            }

            var service = JsonConvert.DeserializeObject<ServiceConfiguration>(File.ReadAllText(path),
                new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
            if (service == null || !Uri.TryCreate(service.BackendUrl, UriKind.Absolute, out var address)
                || address.Scheme != "https" || address.IsLoopback || address.AbsolutePath != "/"
                || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
                throw new InvalidDataException("서비스 주소가 올바르지 않습니다. 홀인원을 다시 설치해 주세요.");
            service.BackendUrl = address.AbsoluteUri;
            return service;
        }
    }

    // One per-PC development address override; authentication is always an account login.
    public sealed class DevelopmentServiceAddress
    {
        public string BackendUrl { get; set; }

        public static string Normalize(string value)
        {
            value = value?.Trim();
            if (IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
                && value.Split('.').Length == 4)
            {
                if (!IPAddress.IsLoopback(ip))
                    throw new InvalidDataException("공인·사설 IP는 https://를 포함한 전체 서비스 주소를 입력해 주세요. IP 단독 입력은 루프백 개발 주소만 지원합니다.");
                value = "http://" + ip + ":5010/";
            }
            if (!Uri.TryCreate(value, UriKind.Absolute, out var address)
                || (address.Scheme != "https" && !(address.Scheme == "http" && address.IsLoopback)) || address.AbsolutePath != "/"
                || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
                throw new InvalidDataException("HTTPS 서비스 주소를 입력해 주세요. HTTP는 로컬 개발 주소에서만 사용할 수 있습니다.");
            return address.AbsoluteUri;
        }

        public static DevelopmentServiceAddress Load(string path)
        {
            if (!File.Exists(path)) return null;
            var settings = JsonConvert.DeserializeObject<DevelopmentServiceAddress>(File.ReadAllText(path),
                new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
            if (settings == null) throw new InvalidDataException("저장된 개발 서버 주소를 읽을 수 없습니다.");
            settings.BackendUrl = Normalize(settings.BackendUrl);
            return settings;
        }

        public static void Save(string path, string address)
        {
            var settings = new DevelopmentServiceAddress { BackendUrl = Normalize(address) };
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(settings, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
