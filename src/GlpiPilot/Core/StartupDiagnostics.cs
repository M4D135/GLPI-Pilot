using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

/// <summary>Best-effort, bounded startup evidence. Diagnostic I/O must never prevent the application from opening.</summary>
public static class StartupDiagnostics
{
    private const int MaximumBytes = 64 * 1024;
    private const int FailureReserve = 12 * 1024;
    private const int LimitNoticeReserve = 512;
    private const int MaximumExceptions = 8;
    private const int MaximumFrames = 16;
    private static readonly object Gate = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly StringBuilder Buffer = new();
    private static Stopwatch? _clock;
    private static string[] _directories = Array.Empty<string>();
    private static string _logPath = "", _latestPath = "", _currentStage = "", _version = "";
    private static int _bytes, _nextDirectory;
    private static bool _isReady, _normalLimited, _fullyLimited, _sinkUnavailable;
    private static DateTime _lastFilenameStamp;

    public static bool IsReady { get { lock (Gate) return _isReady; } }
    public static string LogPath { get { lock (Gate) return _logPath; } }
    public static string CurrentStage { get { lock (Gate) return _currentStage; } }
    public static string Version { get { lock (Gate) return _version; } }

    public static void Start(string version, string? primaryDirectory = null, string? fallbackDirectory = null)
    {
        lock (Gate)
        {
            _clock = Stopwatch.StartNew();
            _isReady = false;
            _normalLimited = _fullyLimited = _sinkUnavailable = false;
            _logPath = _latestPath = "";
            _bytes = _nextDirectory = 0;
            Buffer.Clear();
            _version = SafeText(version, 120);
            _currentStage = "Démarrage";
            _directories = new[] { primaryDirectory ?? DefaultDirectory(false), fallbackDirectory ?? DefaultDirectory(true) }
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Build the header before choosing a sink, so fallback files receive the whole startup context.
            AddBufferedLine("DÉMARRAGE", "GLPI Pilot " + _version);
            AddBufferedLine("ENVIRONNEMENT", "OS=" + SafeValue(() => RuntimeInformation.OSDescription) +
                " ; runtime=" + SafeValue(() => RuntimeInformation.FrameworkDescription) +
                " ; processus=" + RuntimeInformation.ProcessArchitecture + " ; système=" + RuntimeInformation.OSArchitecture +
                " ; PID=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            AddBufferedLine("ÉTAPE", _currentStage);
            _sinkUnavailable = !TryNextSink();
        }
    }

    public static void Step(string stage)
    {
        lock (Gate)
        {
            _currentStage = SafeText(stage, 500);
            Append("ÉTAPE", _currentStage, critical: false);
        }
    }

    public static void MarkReady()
    {
        lock (Gate)
        {
            _isReady = true;
            _currentStage = "Fenêtre prête";
            Append("PRÊT", _currentStage, critical: true);
        }
    }

    public static void CaptureFailure(string stage, Exception error)
    {
        lock (Gate)
        {
            _isReady = false;
            _currentStage = SafeText(stage, 500);
            Append("ÉCHEC", _currentStage, critical: true);
            if (error == null) { Append("EXCEPTION", "Exception absente.", critical: true); return; }
            var chain = new List<Exception>();
            var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            for (Exception? current = error; current != null && chain.Count < MaximumExceptions && seen.Add(current); current = current.InnerException)
                chain.Add(current);
            // Preserve the causal types and messages before allocating space to stack frames.
            for (var index = 0; index < chain.Count; index++)
            {
                var current = chain[index];
                var type = SafeValue(() => current.GetType().FullName ?? current.GetType().Name);
                var message = SafeValue(() => current.Message);
                Append("EXCEPTION", $"#{index + 1} {type} ; HResult=0x{unchecked((uint)current.HResult):X8} ; {message}", critical: true);
            }
            if (chain.Count == MaximumExceptions && chain[^1].InnerException != null)
                Append("EXCEPTION", $"Chaîne limitée aux {MaximumExceptions} premières exceptions.", critical: true);
            Append("ACTION", RecoveryAdvice(error), critical: true);
            for (var index = 0; index < chain.Count && !_fullyLimited; index++)
            {
                try
                {
                    var frames = new StackTrace(chain[index], fNeedFileInfo: false).GetFrames();
                    if (frames == null) continue;
                    foreach (var frame in frames.Take(MaximumFrames))
                    {
                        var method = frame.GetMethod();
                        if (method == null) continue;
                        var structural = (method.DeclaringType?.FullName ?? "<type dynamique>") + "." + method.Name;
                        Append("PILE", $"#{index + 1} " + SafeText(structural, 500), critical: true);
                    }
                    if (frames.Length > MaximumFrames) Append("PILE", $"#{index + 1} pile limitée à {MaximumFrames} méthodes.", critical: true);
                }
                catch (Exception) { Append("PILE", $"#{index + 1} détails structurels indisponibles.", critical: true); }
            }
        }
    }

    public static string RecoveryAdvice(Exception error)
    {
        var chain = new List<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        for (Exception? current = error; current != null && chain.Count < MaximumExceptions && seen.Add(current); current = current.InnerException) chain.Add(current);
        if (chain.Any(item => item is UnauthorizedAccessException))
            return "Vérifiez que votre compte peut écrire dans %LOCALAPPDATA%\\GLPI-Pilot et %TEMP%, puis relancez depuis un dossier local accessible. Faites examiner ce journal si une règle du poste refuse l’accès.";
        if (chain.Any(item => item is BadImageFormatException or DllNotFoundException or FileNotFoundException or FileLoadException))
            return "Extrayez le pack Windows x64 complet dans un nouveau dossier local en conservant GLPI-Pilot.exe avec son dossier wad, puis lancez cet exécutable. Le pack autonome contient son runtime. Joignez le journal si le chargement reste impossible.";
        if (chain.Any(item => item is OutOfMemoryException))
            return "Libérez de la mémoire, fermez les applications inutilisées, puis relancez. Conservez le journal et la taille du relevé qui précédait ce démarrage.";
        if (chain.Any(item => item is IOException))
            return "Vérifiez l’espace disponible et les droits d’écriture des dossiers locaux. Fermez l’outil qui verrouille le fichier indiqué, puis relancez. Conservez vos préférences et ce journal avant toute réparation.";
        if (chain.Any(item => item.GetType().Name.Contains("Xaml", StringComparison.Ordinal)))
            return "Réextrayez le pack complet dans un nouveau dossier local pour éviter un mélange de versions, puis relancez. Joignez ce journal si la construction de l’interface échoue encore.";
        return "Conservez ce journal et l’étape indiquée. Réessayez depuis le pack complet dans un nouveau dossier local ; transmettez le journal si le démarrage reste bloqué. Les sauvegardes de préférences se trouvent dans %LOCALAPPDATA%\\GLPI-Pilot\\Backups.";
    }

    private static string DefaultDirectory(bool temporary)
    {
        try
        {
            var root = temporary ? Path.GetTempPath() : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "GLPI-Pilot", "Logs");
        }
        catch (Exception) { return ""; }
    }

    private static string SafeValue(Func<string> value)
    {
        try { return SafeText(value(), 1200); }
        catch (Exception) { return "[information indisponible]"; }
    }

    private static string SafeText(string? value, int maximum)
    {
        try
        {
            if (string.IsNullOrEmpty(value)) return "";
            var lineEnd = value.IndexOfAny(new[] { '\r', '\n' });
            var text = value[..Math.Min(lineEnd < 0 ? value.Length : lineEnd, 16000)];
            // Headers can contain a scheme plus a token, or several cookie values: mask the entire remainder.
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var timeout = TimeSpan.FromMilliseconds(100);
            text = Regex.Replace(text, @"\b(authorization|proxy-authorization|cookie|set-cookie)\s*[:=]\s*.*", "$1=[masqué]", options, timeout);
            text = Regex.Replace(text, @"\bBearer\s+[^\s,;]+", "Bearer [masqué]", options, timeout);
            text = Regex.Replace(text, @"\b(password|passwd|pwd|token|access_token|id_token|refresh_token|client_secret|secret|SAMLResponse|SAMLRequest|PHPSESSID|_glpi_csrf_token)\b[""']?\s*[:=]\s*(?:""[^""]*""?|'[^']*'?|[^\s,;]+)", "$1=[masqué]", options, timeout);
            text = AppPaths.SafeLog(text);
            text = Regex.Replace(text, @"[a-z]:[/\\]Users[/\\][^/\\\s""']+", "%PROFIL_UTILISATEUR%", options, timeout);
            text = Regex.Replace(text, @"/(?:home|Users)/[^/\s""']+", "%PROFIL_UTILISATEUR%", options, timeout);
            text = new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
            return text.Length <= maximum ? text : text[..maximum] + "…";
        }
        catch (Exception) { return "[message masqué : nettoyage indisponible]"; }
    }

    private static string Line(string kind, string message) =>
        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} +{(_clock?.Elapsed.TotalSeconds ?? 0).ToString("F3", CultureInfo.InvariantCulture)}s [{kind}] {message}{Environment.NewLine}";

    private static void AddBufferedLine(string kind, string message)
    {
        var line = Line(kind, message);
        Buffer.Append(line); _bytes += Utf8.GetByteCount(line);
    }

    private static void Append(string kind, string message, bool critical)
    {
        if (_fullyLimited || !critical && _normalLimited) return;
        var line = Line(kind, message);
        var limit = critical ? MaximumBytes - LimitNoticeReserve : MaximumBytes - FailureReserve;
        if (_bytes + Utf8.GetByteCount(line) > limit)
        {
            if (!critical)
            {
                _normalLimited = true;
                Append("LIMITE", "Étapes supplémentaires omises ; espace réservé au diagnostic d’échec.", critical: true);
            }
            else
            {
                _fullyLimited = true;
                PersistLine(Line("LIMITE", "Journal limité à 64 Kio ; informations supplémentaires omises."));
            }
            return;
        }
        PersistLine(line);
    }

    private static void PersistLine(string line)
    {
        var bytes = Utf8.GetBytes(line);
        if (_bytes + bytes.Length > MaximumBytes) return;
        Buffer.Append(line); _bytes += bytes.Length;
        if (_sinkUnavailable) return;
        try
        {
            using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            // An externally enlarged diagnostic is not made larger by this process.
            if (stream.Length + bytes.Length > MaximumBytes) { _sinkUnavailable = true; return; }
            stream.Write(bytes); stream.Flush();
        }
        catch (Exception)
        {
            _sinkUnavailable = !TryNextSink();
            return;
        }
        WriteLatest();
    }

    private static bool TryNextSink()
    {
        for (; _nextDirectory < _directories.Length; _nextDirectory++)
        {
            try
            {
                var directory = _directories[_nextDirectory];
                Directory.CreateDirectory(directory);
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var now = DateTime.Now;
                    // The filename has millisecond precision, so compare at that same precision.
                    var stamp = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, now.Kind);
                    if (stamp <= _lastFilenameStamp) stamp = _lastFilenameStamp.AddMilliseconds(1);
                    _lastFilenameStamp = stamp;
                    var path = Path.Combine(directory, $"demarrage-{stamp:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}.log");
                    try
                    {
                        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                        { stream.Write(Utf8.GetBytes(Buffer.ToString())); stream.Flush(); }
                        _logPath = path; _latestPath = Path.Combine(directory, "demarrage-dernier.log");
                        _nextDirectory++;
                        WriteLatest();
                        return true;
                    }
                    catch (IOException) when (File.Exists(path)) { }
                }
            }
            catch (Exception) { }
        }
        return false;
    }

    private static void WriteLatest()
    {
        try { File.WriteAllText(_latestPath, Buffer.ToString(), Utf8); }
        catch (Exception) { /* The unique startup log remains usable even if its convenience copy is locked. */ }
    }
}
