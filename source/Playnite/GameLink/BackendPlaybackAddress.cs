using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Playnite.GameLink
{
    // The backend owns the playback page address. Only its short-lived browser capability
    // goes to WebView; native credentials remain in HoleInOneBackendClient.
    public static class BackendPlaybackAddress
    {
        public static Uri Validate(Uri backend, JObject response, DateTimeOffset now)
        {
            if (backend == null || !backend.IsAbsoluteUri ||
                !(backend.Scheme == "https" || backend.Scheme == "http" && backend.IsLoopback) ||
                backend.AbsolutePath != "/" || backend.UserInfo.Length != 0 || backend.Query.Length != 0 || backend.Fragment.Length != 0)
                throw new InvalidDataException("Invalid playback backend origin.");
            if (response == null || response["playbackMode"]?.Type != JTokenType.String ||
                response["playUrl"]?.Type != JTokenType.String || response["expires"]?.Type != JTokenType.Integer)
                throw new InvalidDataException("Playback address contract is incomplete.");
            var mode = (string)response["playbackMode"]; var address = (string)response["playUrl"];
            if (string.IsNullOrWhiteSpace(address) || address.Length > 4096 || address.IndexOfAny(new[] { '\r', '\n', '\t', ' ', '\\' }) >= 0)
                throw new InvalidDataException("Invalid playback capability or address.");
            long expires;
            try { expires = (long)response["expires"]; }
            catch (Exception e) when (e is OverflowException || e is FormatException || e is InvalidCastException)
            { throw new InvalidDataException("Invalid playback expiry."); }
            if (expires <= now.ToUnixTimeMilliseconds()) throw new InvalidDataException("Playback capability expired.");
            if (mode == "aws-hosted-url")
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var hosted) || hosted.Scheme != "https" ||
                    hosted.IdnHost != "gameliftstreams.aws.com" || hosted.Port != 443 || hosted.UserInfo.Length != 0 || hosted.Fragment.Length != 0 ||
                    !Regex.IsMatch(hosted.AbsolutePath, @"^/su-[A-Za-z0-9]+/stream$") || !hosted.Query.StartsWith("?token=", StringComparison.Ordinal) ||
                    hosted.Query.Length <= 7 || hosted.Query.Contains("&") || hosted.Query.Contains(";") ||
                    string.IsNullOrWhiteSpace(Uri.UnescapeDataString(hosted.Query.Substring(7))))
                    throw new InvalidDataException("Invalid AWS hosted playback address.");
                return hosted;
            }
            if (mode != "sdk" || response["ticket"]?.Type != JTokenType.String) throw new InvalidDataException("Unsupported playback mode.");
            var ticket = (string)response["ticket"];
            if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 512 || ticket.IndexOfAny(new[] { '\r', '\n', '\t', ' ' }) >= 0)
                throw new InvalidDataException("Invalid SDK playback capability.");
            Uri target;
            if (address.StartsWith("/", StringComparison.Ordinal))
            {
                if (!address.StartsWith("/#", StringComparison.Ordinal)) throw new InvalidDataException("Invalid relative playback address.");
                if (!Uri.TryCreate(backend, address, out target)) throw new InvalidDataException("Invalid playback address.");
            }
            else if (!Uri.TryCreate(address, UriKind.Absolute, out target)) throw new InvalidDataException("Invalid playback address.");
            if (target.Scheme != backend.Scheme || !string.Equals(target.IdnHost, backend.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                target.Port != backend.Port || target.UserInfo.Length != 0 || target.AbsolutePath != "/" ||
                target.Query.Length != 0 || address.Contains("?") ||
                target.Fragment != "#ticket=" + Uri.EscapeDataString(ticket))
                throw new InvalidDataException("Playback address does not match its backend capability.");
            return target;
        }
    }
    internal static class BackendStartupFailure
    {
        public static string Describe(string code)
        {
            switch (code)
            {
                case "aws_credentials_missing": return "로컬 백엔드의 AWS 로그인 정보가 없습니다. 서버 PC에서 aws login --profile holeinone 실행 후 백엔드를 다시 시작해 주세요.";
                case "aws_credentials_invalid": return "백엔드의 AWS 로그인 정보가 유효하지 않습니다. 서버 PC에서 AWS 로그인 후 백엔드를 다시 시작해 주세요.";
                case "aws_access_denied": return "AWS 계정에 GameLift Streams 권한이 없습니다. 백엔드에서 사용하는 AWS 프로필 권한을 확인해 주세요.";
                case "aws_resource_not_found": return "설정한 GameLift 애플리케이션 또는 스트림 그룹을 찾지 못했습니다. 백엔드의 AWS 리전과 리소스 ID를 확인해 주세요.";
                case "aws_configuration_invalid": return "GameLift Streams 설정을 사용할 수 없습니다. 백엔드의 애플리케이션과 스트림 그룹 설정을 확인해 주세요.";
                case "aws_quota_exceeded": return "GameLift Streams 사용 한도를 초과했습니다. AWS 계정의 스트림 사용량과 한도를 확인해 주세요.";
                default: return "GameLift 스트림을 준비하지 못했습니다. 로컬 백엔드의 상태와 로그를 확인해 주세요.";
            }
        }
    }
}
