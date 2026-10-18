using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Playnite.HoleInOne
{
    // Slay the Spire 2 v0.107.1 settings schema and exported custom user directory.
    public static class Sts2ModSetup
    {
        public static void EnableForCurrentSteamUser()
        {
            var processes = Process.GetProcessesByName("SlayTheSpire2");
            try
            {
                if (processes.Length != 0) throw new InvalidOperationException("실행 중인 로컬 슬더스2를 먼저 종료해야 함.");
            }
            finally { foreach (var process in processes) process.Dispose(); }
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            var accountId = value is int signed ? unchecked((uint)signed) : value is uint unsigned ? unsigned : 0;
            if (accountId == 0) throw new InvalidOperationException("현재 Steam 계정을 확인하지 못함. Steam 로그인이 필요함.");
            Enable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2", "steam",
                (76561197960265728UL + accountId).ToString(System.Globalization.CultureInfo.InvariantCulture), "settings.save"));
        }

        public static void Enable(string settingsPath)
        {
            var existing = File.Exists(settingsPath);
            var settings = existing ? JObject.Parse(File.ReadAllText(settingsPath)) : new JObject { ["schema_version"] = 5 };
            var mods = settings["mod_settings"] as JObject;
            if (mods == null)
            {
                if (settings["mod_settings"] != null && settings["mod_settings"].Type != JTokenType.Null)
                    throw new InvalidDataException("슬더스2 모드 설정 형식이 올바르지 않음.");
                settings["mod_settings"] = mods = new JObject();
            }
            mods["mods_enabled"] = true;
            // Preserve all unrelated settings and the user's choices for other mods.
            if (mods["mod_list"] is JArray list)
                foreach (var entry in list.OfType<JObject>().Where(x => (string)x["id"] == "GameLoader")) entry["is_enabled"] = true;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath)));
            var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, settings.ToString(Formatting.Indented));
                if (existing) File.Replace(temporary, settingsPath, null);
                else File.Move(temporary, settingsPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
