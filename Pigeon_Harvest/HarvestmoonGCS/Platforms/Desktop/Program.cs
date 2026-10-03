using Uno.UI.Hosting;

namespace HarvestmoonGCS;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
