using Microsoft.Win32;
using System;
using System.Globalization;

namespace Playnite.HoleInOne
{
    public static class LocalSteamAccount
    {
        public static uint ActiveAccountId()
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            return value is int signed ? unchecked((uint)signed) : value is uint unsigned ? unsigned : 0;
        }

        public static string SteamId(uint accountId) => accountId == 0 ? null :
            (76561197960265728UL + accountId).ToString(CultureInfo.InvariantCulture);

        public static string RequireSteamId() => SteamId(ActiveAccountId()) ??
            throw new InvalidOperationException("이 PC의 Steam 프로그램에서 로그인 상태를 확인할 수 없습니다. Steam을 열어 HoleInOne에 연결한 계정으로 로그인해 주세요.");
    }
}
