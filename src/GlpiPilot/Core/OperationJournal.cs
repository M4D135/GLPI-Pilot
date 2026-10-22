using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class OperationEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Title { get; set; } = "";
    public string State { get; set; } = "En cours";
    public string Code { get; set; } = "RUNNING";
    public string Detail { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Acknowledged { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public string Duration => FinishedAt.HasValue ? (FinishedAt.Value - StartedAt).ToString(@"hh\:mm\:ss") : "…";
}

public sealed class OperationJournal
{
    private readonly string _path;
    private readonly List<OperationEntry> _entries = new();
    public OperationJournal(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataRoot, "operations.json");
        if (!File.Exists(_path)) return;
        try
        {
            if (new FileInfo(_path).Length > 2 * 1024 * 1024) return;
            _entries.AddRange((JsonSerializer.Deserialize<List<OperationEntry>>(File.ReadAllText(_path)) ?? new()).Where(entry => entry != null).Take(500));
            foreach (var entry in _entries.Where(entry => !entry.FinishedAt.HasValue))
            { entry.State = "Interrompu"; entry.Code = "INTERRUPTED"; entry.Detail = "La session précédente s’est arrêtée avant la fin de cette opération."; entry.FinishedAt = DateTimeOffset.Now; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public IReadOnlyList<OperationEntry> Entries => _entries.OrderByDescending(entry => entry.StartedAt).ToArray();
    public OperationEntry Start(string title, string source)
    {
        var entry = new OperationEntry { Title = AppPaths.SafeLog(title), Source = AppPaths.SafeUrl(source) };
        _entries.Insert(0, entry); if (_entries.Count > 500) _entries.RemoveRange(500, _entries.Count - 500); Save(); return entry;
    }
    public void Finish(OperationEntry entry, string state, string code, string detail)
    { entry.State = state; entry.Code = code; entry.Detail = AppPaths.SafeLog(detail); entry.FinishedAt = DateTimeOffset.Now; Save(); }
    public void Acknowledge(string id)
    { var entry = _entries.FirstOrDefault(candidate => candidate.Id == id); if (entry != null) { entry.Acknowledged = true; Save(); } }
    public void Snooze(string id, TimeSpan duration)
    { var entry = _entries.FirstOrDefault(candidate => candidate.Id == id); if (entry != null) { entry.SnoozedUntil = DateTimeOffset.Now + duration; Save(); } }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(_entries)); File.Move(temporary, _path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppPaths.AppendLog("JOURNAL · sauvegarde locale indisponible : " + AppPaths.SafeError(error)); }
    }
    public static string ErrorCode(Exception error) => error.GetBaseException() switch
    {
        OperationCanceledException => "CANCELLED", UnauthorizedAccessException => "ACCESS_DENIED",
        FileNotFoundException => "FILE_MISSING", InvalidDataException => "DATA_INVALID",
        TimeoutException => "TIMEOUT", IOException => "IO_ERROR", _ => "OPERATION_FAILED"
    };
}
