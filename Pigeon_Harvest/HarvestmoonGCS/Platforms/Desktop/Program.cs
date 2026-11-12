using Uno.UI.Hosting;

namespace HarvestmoonGCS;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // MoonHarvest Master Security & Access Verification (First Guard)
        var lockResult = HarvestmoonGCS.Services.Security.MasterLockService.VerifyAccess();

        if (args.Length > 0 && args[0] == "--hwid")
        {
            Console.WriteLine($"MoonHarvest Hardware ID: {lockResult.HardwareId}");
            Environment.ExitCode = 0;
            return;
        }

        if (args.Length > 0 && args[0] == "--verify-license")
        {
            Console.WriteLine($"Akses Lisensi: {(lockResult.IsAllowed ? "DIIZINKAN" : "TERKUNCI")}");
            Console.WriteLine($"Keterangan: {lockResult.Message}");
            Console.WriteLine($"Hardware ID: {lockResult.HardwareId}");
            Environment.ExitCode = lockResult.IsAllowed ? 0 : 403;
            return;
        }

        if (!lockResult.IsAllowed)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║            MOONHARVEST GCS — CRITICAL SECURITY LOCKOUT               ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine($"[AKSES DITOLAK] {lockResult.Message}");
            Console.WriteLine($"[HARDWARE ID]  {lockResult.HardwareId}");
            Console.WriteLine("Program tidak dapat dijalankan karena lisensi dinonaktifkan oleh pemilik.");
            Console.ResetColor();
            Environment.ExitCode = 403;
            return;
        }

        if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")))
        {
            Environment.SetEnvironmentVariable("PATH",
                AppContext.BaseDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        }
        if (args.Length > 0 && args[0] == "--check-runtime")
        {
            Environment.ExitCode = RuntimeReadinessCheck.Run(args.Length > 1 ? args[1] :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MoonHarvest-check.json"));
            return;
        }

        App.InitializeLogging();

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();

        host.Run();
    }
}
