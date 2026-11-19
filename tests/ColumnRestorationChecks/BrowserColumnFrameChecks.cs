using System.Reflection;
using System.Runtime.ExceptionServices;
using GlpiPilot.Core;
using Microsoft.Playwright;

// Exercise the real observer's admission rules without opening a browser.
// The interfaces support only identity properties and event subscriptions;
// a native read, navigation or preference command would fail this test.
internal static class BrowserColumnFrameChecks
{
    public static void Run()
    {
        const string origin = "https://glpi.test";
        var page = ColumnFrameStub.Create<IPage>(new() { ["IsClosed"] = false });
        var table = ColumnFrameStub.Create<IFrame>(new()
        {
            ["Page"] = page, ["Url"] = origin + "/glpi/front/ticket.php", ["ParentFrame"] = null, ["IsDetached"] = false
        });
        var values = new Dictionary<string, object?>
        {
            ["Page"] = page, ["Url"] = "", ["ParentFrame"] = table, ["IsDetached"] = false
        };
        var child = ColumnFrameStub.Create<IFrame>(values);
        var type = typeof(BrowserService).GetNestedType("ColumnPreparationActivity", BindingFlags.NonPublic)!;
        var bind = type.GetMethod("BindPreferencesFrame")!;
        using var observer = (IDisposable)Activator.CreateInstance(type, new object?[] { page, table, origin, 5, null, false })!;
        void Bind(IFrame frame)
        {
            try { bind.Invoke(observer, new object[] { frame }); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }
        static void Refuse(Action action, string reason)
        {
            try { action(); }
            catch (InvalidOperationException error) when (error.Message.Contains("n’appartient plus au contexte", StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("The observer accepted " + reason + ".");
        }

        // Attached -> initial blank document -> verified native document.
        Bind(child);
        values["Url"] = "about:blank"; Bind(child);
        values["Url"] = origin + "/glpi/front/displaypreference.form.php?itemtype=Ticket"; Bind(child);
        foreach (var url in new[]
        {
            "https://other-glpi.test/glpi/front/displaypreference.form.php?itemtype=Ticket",
            "http://glpi.test/glpi/front/displaypreference.form.php?itemtype=Ticket",
            "https://user@glpi.test/glpi/front/displaypreference.form.php?itemtype=Ticket",
            "data:text/html,preferences", "about:srcdoc"
        })
        { values["Url"] = url; Refuse(() => Bind(child), "an unverified frame address"); }

        // An initial blank address never exempts structural ownership checks.
        values["Url"] = "";
        values["Page"] = ColumnFrameStub.Create<IPage>();
        Refuse(() => Bind(child), "a frame belonging to another page");
        values["Page"] = page;
        values["ParentFrame"] = ColumnFrameStub.Create<IFrame>();
        Refuse(() => Bind(child), "a frame belonging to another parent");
        values["ParentFrame"] = table; values["IsDetached"] = true;
        Refuse(() => Bind(child), "a detached initial frame");
        values["IsDetached"] = false; Bind(child);
        Refuse(() => Bind(table), "the collection document in place of its child preferences frame");
    }
}

// Public parameterless construction is required by DispatchProxy.
public class ColumnFrameStub : DispatchProxy
{
    private Dictionary<string, object?> _values = new();

    public static T Create<T>(Dictionary<string, object?>? values = null) where T : class
    {
        var proxy = Create<T, ColumnFrameStub>();
        ((ColumnFrameStub)(object)proxy)._values = values ?? new();
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        if (name.StartsWith("get_", StringComparison.Ordinal) && _values.TryGetValue(name[4..], out var value)) return value;
        throw new InvalidOperationException("Unexpected browser operation in a frame observer test: " + name);
    }
}
