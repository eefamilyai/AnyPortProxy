using AnyPortProxy.CatchAll;
using AnyPortProxy.Core;
using AnyPortProxy.Logging;
using AnyPortProxy.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace AnyPortProxy;

internal static class ProxyHost
{
    public static void Run(string[] args)
    {
        var files = new FileLoggerProvider(AppPaths.LogDir);

        // Last line of defence: anything that slips through is written down before the process dies,
        // and Windows' service recovery restarts us (configured by the installer).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            files.WriteNow($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} CRIT Fatal: unhandled error, restarting: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            files.WriteNow($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} WARN Background task error (ignored): {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };

        // Bursts of thousands of connections shouldn't wait for the thread pool to warm up.
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, Environment.ProcessorCount * 4), Math.Max(io, Environment.ProcessorCount * 4));

        // Services start with CWD = System32, so anchor everything to the exe's folder.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        try { ConfigStore.EnsureExists(); } catch { }
        // Only the Logging section comes from IConfiguration; proxy settings go through ConfigMonitor,
        // which keeps the last good settings when the file is broken.
        if (Directory.Exists(AppPaths.DataDir))
        {
            builder.Configuration.AddJsonFile(s =>
            {
                s.FileProvider = new PhysicalFileProvider(AppPaths.DataDir);
                s.Path = "config.json";
                s.Optional = true;
                s.ReloadOnChange = true;
                s.OnLoadException = ctx => ctx.Ignore = true;
            });
        }

        builder.Services.AddWindowsService(o => o.ServiceName = AppPaths.ServiceName);
        builder.Services.Configure<HostOptions>(o =>
        {
            // One failing component must never stop the others.
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
            o.ShutdownTimeout = TimeSpan.FromSeconds(10);
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(files);
        if (!WindowsServiceHelpers.IsWindowsService())
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });

        builder.Services.AddSingleton<ConfigMonitor>();
        builder.Services.AddSingleton<Router>();
        builder.Services.AddSingleton<FlowTable>();
        builder.Services.AddSingleton<StatusTracker>();
        builder.Services.AddSingleton<ConnectionGate>();
        builder.Services.AddSingleton<LogLimiter>();
        builder.Services.AddHostedService<SniffingProxyService>();
        builder.Services.AddHostedService<CatchAllProxyService>();
        builder.Services.AddHostedService<PortForwardService>();
        builder.Services.AddHostedService<StatusWriterService>();
        builder.Services.AddHostedService<SelfHealService>();
        builder.Services.AddHostedService<UpdateService>();

        builder.Build().Run();
    }
}
