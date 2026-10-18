using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Playnite.HoleInOne
{
    public static class LegendaryTool
    {
        public static string Executable => Path.Combine(PlaynitePaths.ProgramPath, "Tools", "Legendary", "legendary.exe");
        public static string DataDirectory => Path.Combine(PlaynitePaths.ConfigRootPath, "HoleInOne", "Epic");

        private static ProcessStartInfo StartInfo(string[] arguments, bool interactive)
        {
            if (!File.Exists(Executable)) throw new FileNotFoundException("Epic 다운로드 도구가 없음. Prepare-HoleInOneTools.ps1 실행이 필요함.");
            Directory.CreateDirectory(DataDirectory);
            var info = new ProcessStartInfo(Executable, string.Join(" ", arguments.Select(Quote)))
            {
                UseShellExecute = false, CreateNoWindow = !interactive, WorkingDirectory = DataDirectory,
                RedirectStandardOutput = !interactive, RedirectStandardError = !interactive,
                RedirectStandardInput = !interactive
            };
            info.EnvironmentVariables["LEGENDARY_CONFIG_PATH"] = DataDirectory;
            return info;
        }

        public static async Task<string> RunAsync(params string[] arguments)
        {
            using (var process = Process.Start(StartInfo(arguments, false)))
            {
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false);
                await errors.ConfigureAwait(false); // Drain diagnostics without persisting credentials or launch tokens.
                if (process.ExitCode != 0) throw new InvalidOperationException("Epic 요청을 완료하지 못함. 계정 연결과 저장 공간을 확인해야 함.");
                return text;
            }
        }

        public static string Quote(string value)
        {
            if (value == null || value.Any(c => c == '\r' || c == '\n' || c == '\0')) throw new ArgumentException("Invalid process argument.");
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
