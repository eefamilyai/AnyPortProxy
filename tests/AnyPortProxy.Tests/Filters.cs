using AnyPortProxy.CatchAll;
using AnyPortProxy.Core;

/// <summary>
/// Compiles every kind of filter the app can generate with the real WinDivert.dll parser
/// (needs WinDivert.dll next to the test exe; no admin or driver needed).
/// </summary>
static class Filters
{
    public static int Run()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "WinDivert.dll")))
        {
            Console.WriteLine("SKIPPED: WinDivert.dll not found (run build.ps1 once to download it)");
            return 0;
        }

        var cases = new List<(string Name, string Filter)>();
        void Add(string name, CatchAllOptions c, IEnumerable<int> tcpOwn, IEnumerable<int> udpOwn, bool udp) =>
            cases.Add((name, FilterBuilder.Build(c, tcpOwn, udpOwn, udp)));

        var defaults = ConfigStore.CreateDefault().Proxy.CatchAll;
        Add("defaults, TCP+UDP", defaults, [80, 443], [], true);
        Add("defaults, TCP only", defaults, [80, 443], [], false);
        Add("home network included", new CatchAllOptions { InterceptLan = true, BlockedPorts = [.. CatchAllOptions.DefaultBlocked] }, [80, 443], [], true);
        Add("user's setup (50131 forwarded, game port 25565)", new CatchAllOptions { AllowedPorts = "1-49151, 50131", BlockedPorts = [.. CatchAllOptions.DefaultBlocked] },
            [80, 443, 25565], [], true);
        Add("several ranges + single ports", new CatchAllOptions { AllowedPorts = "3000-3999, 8080, 25565, 27015-27030", BlockedPorts = [22] }, [80, 443], [], true);
        Add("big port rules (merged runs)", new CatchAllOptions { BlockedPorts = [.. CatchAllOptions.DefaultBlocked] },
            [80, 443, .. Enumerable.Range(10000, 1000), .. Enumerable.Range(20000, 500)], [.. Enumerable.Range(30000, 1000), 51820], true);
        Add("nothing blocked", new CatchAllOptions { BlockedPorts = [] }, [], [], true);
        Add("single allowed port", new CatchAllOptions { AllowedPorts = "25565", BlockedPorts = [] }, [], [], true);
        Add("custom internal port", new CatchAllOptions { ListenPort = 40000, BlockedPorts = [3389] }, [80, 443], [], true);
        // Many scattered port rules: stress the filter size limit.
        Add("many scattered port rules", new CatchAllOptions { BlockedPorts = [.. CatchAllOptions.DefaultBlocked] },
            [80, 443, .. Enumerable.Range(0, 40).Select(i => 2000 + i * 7)], [.. Enumerable.Range(0, 40).Select(i => 4000 + i * 7)], true);

        int failed = 0;
        foreach (var (name, filter) in cases)
        {
            var error = WinDivert.Validate(filter);
            if (error is null) Console.WriteLine($"OK    {name} ({filter.Length} chars)");
            else
            {
                failed++;
                int pos = int.TryParse(error.Split(':')[0].Replace("position ", ""), out var p) ? p : 0;
                Console.WriteLine($"FAIL  {name}: {error}\n      ...{filter[Math.Max(0, pos - 40)..Math.Min(filter.Length, pos + 40)]}...");
            }
        }
        Console.WriteLine(failed == 0 ? $"ALL {cases.Count} FILTERS ACCEPTED BY WINDIVERT" : $"{failed} FILTER(S) REJECTED");
        return failed;
    }
}
