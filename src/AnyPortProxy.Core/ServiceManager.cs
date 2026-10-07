using System.ServiceProcess;
using Microsoft.Win32;

namespace AnyPortProxy.Core;

public enum ServiceState { NotInstalled, Stopped, Starting, Running, Stopping }

public static class ServiceManager
{
    public static ServiceState GetState()
    {
        try
        {
            using var sc = new ServiceController(AppPaths.ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceState.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ServiceState.Stopping,
                _ => ServiceState.Stopped,
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotInstalled;
        }
    }

    public static void Start()
    {
        using var sc = new ServiceController(AppPaths.ServiceName);
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public static void Stop()
    {
        using var sc = new ServiceController(AppPaths.ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    public static void Restart()
    {
        Stop();
        Start();
    }

    public static string? ImagePath() =>
        Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{AppPaths.ServiceName}", "ImagePath", null) as string;
}
