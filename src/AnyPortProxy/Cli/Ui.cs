using AnyPortProxy.Core;
using Spectre.Console;

namespace AnyPortProxy.Cli;

internal static class Ui
{
    public static string E(string? s) => Markup.Escape(s ?? "");

    public static bool Interactive => AnsiConsole.Profile.Capabilities.Interactive && !Console.IsInputRedirected;

    public static void Ok(string s) => AnsiConsole.MarkupLine($"[green]✔[/] {E(s)}");
    public static void Info(string s) => AnsiConsole.MarkupLine($"[deepskyblue1]ℹ[/] {E(s)}");
    public static void Warn(string s) => AnsiConsole.MarkupLine($"[yellow]⚠[/] {E(s)}");
    public static void Error(string s) => AnsiConsole.MarkupLine($"[red]✖[/] {E(s)}");
    public static void Hint(string s) => AnsiConsole.MarkupLine($"  [grey]{E(s)}[/]");

    public static void Banner() =>
        AnsiConsole.Write(new Rule($"[bold deepskyblue1]AnyPortProxy[/] [grey]v{E(AppPaths.Version)}[/]").LeftJustified());

    public static string Icon(CheckStatus s) => s switch
    {
        CheckStatus.Ok => "[green]✔[/]",
        CheckStatus.Info => "[deepskyblue1]ℹ[/]",
        CheckStatus.Warn => "[yellow]⚠[/]",
        _ => "[red]✖[/]",
    };

    public static void Result(CheckResult r)
    {
        AnsiConsole.MarkupLine($"{Icon(r.Status)} {(r.Status is CheckStatus.Fail or CheckStatus.Warn ? "[bold]" + E(r.Title) + "[/]" : E(r.Title))}");
        if (!string.IsNullOrWhiteSpace(r.Detail)) AnsiConsole.MarkupLine($"   [grey]{E(r.Detail)}[/]");
        if (r.Fix is not null) AnsiConsole.MarkupLine($"   [blue]Can fix automatically:[/] {E(r.Fix.Label)}");
    }

    public static void Results(IEnumerable<CheckResult> results)
    {
        foreach (var r in results) Result(r);
    }

    public static Task<T> Busy<T>(string message, Func<Action<string>, Task<T>> work) =>
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync(E(message), ctx => work(s => ctx.Status(E(s))));

    public static void PressAnyKey()
    {
        if (!Interactive) return;
        AnsiConsole.MarkupLine("\n[grey]Press any key to continue…[/]");
        Console.ReadKey(true);
    }

    /// <summary>Asks which fixes to apply (or applies all), then runs them.</summary>
    public static async Task OfferFixesAsync(IReadOnlyList<CheckResult> results, bool applyAll, string fixCommand = "apx check --fix")
    {
        var fixes = results.Where(r => r.Fix is not null).ToList();
        if (fixes.Count == 0) return;

        List<CheckResult> chosen;
        if (applyAll)
        {
            chosen = fixes;
        }
        else if (Interactive)
        {
            AnsiConsole.WriteLine();
            var prompt = new MultiSelectionPrompt<CheckResult>()
                .Title($"[bold]{fixes.Count} problem(s) can be fixed automatically.[/] Which ones should I fix?")
                .InstructionsText("[grey](space = toggle, enter = go)[/]")
                .NotRequired()
                .UseConverter(r => E($"{r.Fix!.Label}  —  {r.Title}"))
                .AddChoices(fixes);
            foreach (var f in fixes) prompt.Select(f);
            chosen = AnsiConsole.Prompt(prompt);
        }
        else
        {
            Info($"{fixes.Count} problem(s) can be fixed automatically. Run: {fixCommand}");
            return;
        }

        if (chosen.Count > 0 && !Elevation.IsAdmin)
        {
            Warn($"Fixing needs Administrator rights. Run: {fixCommand} (it will ask Windows for permission).");
            return;
        }
        foreach (var r in chosen)
        {
            try
            {
                var msg = await r.Fix!.Run();
                Ok(msg);
            }
            catch (Exception ex)
            {
                Error($"{r.Fix!.Label} failed: {ex.Message}");
            }
        }
    }
}
