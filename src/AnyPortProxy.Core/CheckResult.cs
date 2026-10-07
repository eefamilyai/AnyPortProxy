namespace AnyPortProxy.Core;

public enum CheckStatus { Ok, Info, Warn, Fail }

/// <summary>Something the app can fix with one click. Run returns a short message describing what it did.</summary>
public sealed record FixAction(string Label, Func<Task<string>> Run);

public sealed record CheckResult(CheckStatus Status, string Title, string? Detail = null, FixAction? Fix = null)
{
    public static CheckResult Ok(string title, string? detail = null) => new(CheckStatus.Ok, title, detail);
    public static CheckResult Info(string title, string? detail = null, FixAction? fix = null) => new(CheckStatus.Info, title, detail, fix);
    public static CheckResult Warn(string title, string? detail = null, FixAction? fix = null) => new(CheckStatus.Warn, title, detail, fix);
    public static CheckResult Fail(string title, string? detail = null, FixAction? fix = null) => new(CheckStatus.Fail, title, detail, fix);
}
