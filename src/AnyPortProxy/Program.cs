using AnyPortProxy;
using AnyPortProxy.Cli;
using Microsoft.Extensions.Hosting.WindowsServices;

// One exe, two jobs: the background proxy ("run" / when started as a service) and the "apx" command line.
if (WindowsServiceHelpers.IsWindowsService())
{
    ProxyHost.Run(args);
    return 0;
}
if (args.Length > 0 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
{
    ProxyHost.Run(args[1..]);
    return 0;
}
return await CliApp.RunAsync(args);
