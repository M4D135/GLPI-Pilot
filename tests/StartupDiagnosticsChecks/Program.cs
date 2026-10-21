using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GlpiPilot.Core;

var suite = new StartupChecks();
return suite.Run();

internal sealed class StartupChecks
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "glpi-startup-checks-" + Guid.NewGuid().ToString("N"));
    private int _passed, _failed;

    public int Run()
    {
        Directory.CreateDirectory(_root);
        try
        {
            Check("start creates unique and latest logs with environment context", StartContext);
            Check("steps append timestamps, duration and a sanitized current stage", StepContext);
            Check("ready is explicit and a failure resets it", ReadyState);
            Check("exception chain retains types, HResult and structural stack only", ExceptionEvidence);
            Check("first-line messages redact tokens, URL queries and user profiles", SensitiveEvidence);
            Check("exception chains are bounded", ChainLimit);
            Check("unreliable exception messages never invoke raw ToString", UnreliableException);
            Check("an unavailable primary directory falls back", InitialFallback);
            Check("a later write failure preserves prior steps in the fallback log", WriteFallback);
            Check("both unavailable directories never prevent state transitions", NoWritableDirectory);
            Check("an unavailable latest copy does not prevent the unique log", LatestCopyFailure);
            Check("rapid restarts preserve separate append-only logs", UniqueRestarts);
            Check("64 KiB bound reserves space for failure evidence", SizeLimit);
            Check("concurrent steps remain complete and synchronized", ConcurrentSteps);
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"Startup diagnostics: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private void Check(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }

    private string DirectoryFor(string name) => Path.Combine(_root, name);
    private static string Log() => File.ReadAllText(StartupDiagnostics.LogPath);
    private void Start(string name) => StartupDiagnostics.Start("1.3.1-test", DirectoryFor(name), DirectoryFor(name + "-fallback"));
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Contains(string text, string expected)
    { Require(text.Contains(expected, StringComparison.Ordinal), "Missing expected text: " + expected); }
    private static void Absent(string text, string secret)
    { Require(!text.Contains(secret, StringComparison.Ordinal), "A synthetic secret or forbidden detail was retained."); }

    private void StartContext()
    {
        Start("context");
        Require(!StartupDiagnostics.IsReady, "Start must not declare the UI ready.");
        Require(StartupDiagnostics.Version == "1.3.1-test", "Version not retained.");
        Require(StartupDiagnostics.CurrentStage == "Démarrage", "Initial stage not retained.");
        Require(Regex.IsMatch(Path.GetFileName(StartupDiagnostics.LogPath), @"^demarrage-\d{8}-\d{9}-\d+\.log$"), "Unexpected log filename.");
        var log = Log();
        foreach (var expected in new[] { "[DÉMARRAGE] GLPI Pilot 1.3.1-test", "OS=", "runtime=", "processus=", "système=", "PID=" }) Contains(log, expected);
        Require(File.ReadAllText(Path.Combine(DirectoryFor("context"), "demarrage-dernier.log")) == log, "Latest copy differs.");
    }

    private void StepContext()
    {
        Start("steps");
        var initial = Log();
        StartupDiagnostics.Step("Ressources chargées\r\nSHOULD_NOT_BE_LOGGED");
        var log = Log();
        Require(log.StartsWith(initial, StringComparison.Ordinal), "Existing lines were not preserved.");
        Require(!StartupDiagnostics.IsReady && StartupDiagnostics.CurrentStage == "Ressources chargées", "Step state is wrong.");
        Require(Regex.IsMatch(log, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \+\d+\.\d{3}s \[ÉTAPE\] Ressources chargées"), "Step timestamp or elapsed duration is missing.");
        Absent(log, "SHOULD_NOT_BE_LOGGED");
    }

    private void ReadyState()
    {
        Start("ready");
        StartupDiagnostics.MarkReady();
        Require(StartupDiagnostics.IsReady && StartupDiagnostics.CurrentStage == "Fenêtre prête", "MarkReady state is wrong.");
        Contains(Log(), "[PRÊT] Fenêtre prête");
        StartupDiagnostics.CaptureFailure("Construction fenêtre", new InvalidOperationException("synthetic failure"));
        Require(!StartupDiagnostics.IsReady && StartupDiagnostics.CurrentStage == "Construction fenêtre", "Failure must reset ready.");
        Contains(Log(), "[ÉCHEC] Construction fenêtre");
        Start("ready-restart");
        Require(!StartupDiagnostics.IsReady, "Start must reset a previous run.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInner() => throw new ArgumentException("inner first line\nLOCAL_SYNTHETIC_DETAIL");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception WrappedFailure()
    {
        try
        {
            try { ThrowInner(); }
            catch (Exception error) { throw new InvalidOperationException("outer failure", error); }
        }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Fixture did not throw.");
    }

    private void ExceptionEvidence()
    {
        Start("exceptions");
        var error = WrappedFailure();
        StartupDiagnostics.CaptureFailure("Initialisation", error);
        var log = Log();
        Contains(log, "#1 System.InvalidOperationException");
        Contains(log, "#2 System.ArgumentException");
        Contains(log, "HResult=0x" + unchecked((uint)error.HResult).ToString("X8", CultureInfo.InvariantCulture));
        Contains(log, "HResult=0x" + unchecked((uint)error.InnerException!.HResult).ToString("X8", CultureInfo.InvariantCulture));
        Contains(log, "[PILE] #1 StartupChecks.WrappedFailure");
        Contains(log, "[PILE] #2 StartupChecks.ThrowInner");
        Absent(log, "LOCAL_SYNTHETIC_DETAIL");
        Absent(log, "Program.cs");
        Absent(log, _root);
        Absent(log, " at ");
    }

    private void SensitiveEvidence()
    {
        StartupDiagnostics.Start("test-token=VERSION_SECRET", DirectoryFor("secrets"), DirectoryFor("secrets-fallback"));
        StartupDiagnostics.Step("path https://alice:URL_PASSWORD@example.invalid/front/ticket.form.php?id=QUERY_SECRET#FRAGMENT_SECRET");
        StartupDiagnostics.Step("authorization: Bearer AUTH_SECRET cookie=COOKIE_SECRET");
        StartupDiagnostics.Step("cookie: PHPSESSID=COOKIE_ONE; session=COOKIE_TWO");
        StartupDiagnostics.Step("Bearer BEARER_SECRET");
        StartupDiagnostics.Step("pwd=PWD_SECRET; token=TOKEN_SECRET; _glpi_csrf_token=CSRF_SECRET; PHPSESSID=SESSION_SECRET");
        StartupDiagnostics.Step("\"access_token\":\"ACCESS_SECRET\", id_token=ID_SECRET refresh_token=REFRESH_SECRET client_secret=CLIENT_SECRET");
        StartupDiagnostics.Step("password=\"PASSWORD WITH SPACES\"; secret='SECRET WITH SPACES'; SAMLResponse=SAML_SECRET SAMLRequest=REQUEST_SECRET");
        StartupDiagnostics.Step("Profile C:\\Users\\AlicePrivate\\AppData\\Local; C:/Users/BobPrivate/AppData; /home/CarolPrivate/settings");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 1) StartupDiagnostics.Step("Actual profile=" + profile + "/config");
        StartupDiagnostics.CaptureFailure("failure token=STAGE_SECRET", new InvalidOperationException("URL=https://example.invalid/?q=ERROR_QUERY token=ERROR_TOKEN\nSECOND_LINE_SECRET"));
        var log = Log();
        foreach (var secret in new[] { "VERSION_SECRET", "URL_PASSWORD", "QUERY_SECRET", "FRAGMENT_SECRET", "AUTH_SECRET", "COOKIE_SECRET", "COOKIE_ONE", "COOKIE_TWO", "BEARER_SECRET", "PWD_SECRET", "TOKEN_SECRET", "CSRF_SECRET", "SESSION_SECRET", "ACCESS_SECRET", "ID_SECRET", "REFRESH_SECRET", "CLIENT_SECRET", "PASSWORD WITH SPACES", "SECRET WITH SPACES", "SAML_SECRET", "REQUEST_SECRET", "AlicePrivate", "BobPrivate", "CarolPrivate", "STAGE_SECRET", "ERROR_QUERY", "ERROR_TOKEN", "SECOND_LINE_SECRET" }) Absent(log, secret);
        if (profile.Length > 1) Absent(log, profile);
        Contains(log, "https://example.invalid/front/ticket.form.php");
        Contains(log, "%PROFIL_UTILISATEUR%");
        Contains(log, "[masqué]");
        Absent(StartupDiagnostics.Version, "VERSION_SECRET");
        Absent(StartupDiagnostics.CurrentStage, "STAGE_SECRET");
    }

    private void ChainLimit()
    {
        Start("chain");
        Exception error = new Exception("last cause");
        for (var index = 0; index < 20; index++) error = new InvalidOperationException("wrapper " + index, error);
        StartupDiagnostics.CaptureFailure("Nested", error);
        var log = Log();
        Require(Regex.Matches(log, @"\[EXCEPTION\] #\d+ ").Count == 8, "Exception chain was not bounded to eight.");
        Contains(log, "Chaîne limitée aux 8 premières exceptions.");
        Absent(log, "last cause");
    }

    private void UnreliableException()
    {
        Start("bad-message");
        StartupDiagnostics.CaptureFailure("Failure", new UnreliableMessageException());
        Contains(Log(), nameof(UnreliableMessageException));
        Contains(Log(), "[information indisponible]");
        Require(!StartupDiagnostics.IsReady, "Failure was incorrectly marked ready.");
    }

    private void InitialFallback()
    {
        var blocked = DirectoryFor("blocked-file");
        File.WriteAllText(blocked, "not a directory");
        var fallback = DirectoryFor("initial-fallback");
        StartupDiagnostics.Start("test", blocked, fallback);
        Require(Path.GetDirectoryName(StartupDiagnostics.LogPath) == fallback, "Fallback was not selected.");
        StartupDiagnostics.Step("Fallback step");
        Contains(Log(), "[DÉMARRAGE] GLPI Pilot test");
        Contains(Log(), "[ÉTAPE] Fallback step");
    }

    private void WriteFallback()
    {
        var primary = DirectoryFor("later-primary");
        var fallback = DirectoryFor("later-fallback");
        StartupDiagnostics.Start("test", primary, fallback);
        StartupDiagnostics.Step("Before write failure");
        Directory.Move(primary, primary + "-moved");
        File.WriteAllText(primary, "directory no longer available");
        StartupDiagnostics.Step("After write failure");
        Require(Path.GetDirectoryName(StartupDiagnostics.LogPath) == fallback, "Write failure did not switch to fallback.");
        var log = Log();
        Contains(log, "Before write failure");
        Contains(log, "After write failure");
        Require(File.ReadAllText(Path.Combine(fallback, "demarrage-dernier.log")) == log, "Fallback latest copy differs.");
    }

    private void NoWritableDirectory()
    {
        var first = DirectoryFor("not-directory-1");
        var second = DirectoryFor("not-directory-2");
        File.WriteAllText(first, "blocked"); File.WriteAllText(second, "blocked");
        StartupDiagnostics.Start("test", first, second);
        Require(StartupDiagnostics.LogPath.Length == 0 && !StartupDiagnostics.IsReady, "Unavailable diagnostic state is wrong.");
        StartupDiagnostics.Step("Still running");
        StartupDiagnostics.MarkReady();
        Require(StartupDiagnostics.IsReady, "Unavailable log prevented readiness.");
        StartupDiagnostics.CaptureFailure("After ready", new Exception("failure"));
        Require(!StartupDiagnostics.IsReady && StartupDiagnostics.CurrentStage == "After ready", "Unavailable log prevented failure state.");
        StartupDiagnostics.Start("test", "bad\0path", "bad\0fallback");
        StartupDiagnostics.Step("Invalid path is nonfatal");
        Require(StartupDiagnostics.LogPath.Length == 0, "Invalid paths should not create a log.");
    }

    private void LatestCopyFailure()
    {
        var primary = DirectoryFor("latest-blocked");
        Directory.CreateDirectory(Path.Combine(primary, "demarrage-dernier.log"));
        StartupDiagnostics.Start("test", primary, DirectoryFor("latest-fallback"));
        StartupDiagnostics.Step("Unique log continues");
        Require(Path.GetDirectoryName(StartupDiagnostics.LogPath) == primary, "A blocked convenience copy unnecessarily discarded the unique log.");
        Contains(Log(), "Unique log continues");
    }

    private void UniqueRestarts()
    {
        var primary = DirectoryFor("rapid");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < 30; index++)
        {
            StartupDiagnostics.Start("run-" + index, primary, DirectoryFor("rapid-fallback"));
            var path = StartupDiagnostics.LogPath;
            Require(Path.GetDirectoryName(path) == primary, "Rapid restart incorrectly exhausted its primary path.");
            Require(paths.Add(path), "A startup filename was reused.");
            contents[path] = Log();
        }
        foreach (var entry in contents) Require(File.ReadAllText(entry.Key) == entry.Value, "A previous startup log was overwritten.");
    }

    private void SizeLimit()
    {
        Start("bounded");
        for (var index = 0; index < 300; index++) StartupDiagnostics.Step("Étape " + index + " " + new string('é', 480));
        var normalLog = Log();
        Contains(normalLog, "espace réservé au diagnostic d’échec");
        StartupDiagnostics.CaptureFailure("Reserved failure evidence", WrappedFailure());
        var log = Log();
        Contains(log, "Reserved failure evidence");
        Contains(log, "System.InvalidOperationException");
        for (var index = 0; index < 150; index++) StartupDiagnostics.CaptureFailure("Additional failure", new Exception(new string('é', 1200)));
        log = Log();
        Contains(log, "Journal limité à 64 Kio");
        Require(new FileInfo(StartupDiagnostics.LogPath).Length <= 64 * 1024, "Unique log exceeded 64 KiB.");
        var latest = Path.Combine(DirectoryFor("bounded"), "demarrage-dernier.log");
        Require(new FileInfo(latest).Length <= 64 * 1024 && File.ReadAllText(latest) == log, "Latest copy exceeded its limit or differs.");
        Require(!StartupDiagnostics.IsReady, "A limited log incorrectly changed failure state.");
    }

    private void ConcurrentSteps()
    {
        Start("concurrent");
        Parallel.For(0, 40, index => StartupDiagnostics.Step("Parallel step " + index.ToString("D2", CultureInfo.InvariantCulture)));
        var log = Log();
        for (var index = 0; index < 40; index++) Require(Regex.Matches(log, "\\[ÉTAPE\\] Parallel step " + index.ToString("D2", CultureInfo.InvariantCulture) + "(?:\\r?\\n)").Count == 1, "A concurrent step was lost or duplicated.");
    }
}

internal sealed class UnreliableMessageException : Exception
{
    public override string Message => throw new InvalidOperationException("message getter unavailable");
    public override string ToString() => throw new InvalidOperationException("raw ToString must never be used");
}
