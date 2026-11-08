using System.Diagnostics;
using System.IO;

namespace GlpiPilot.Core;

/// <summary>Workstation-local collection policy, separate from replica synchronization.</summary>
public sealed class LibraryCollectionOptions
{
    public int Version { get; set; } = 1;
    public bool Automatic { get; set; }
    public int IntervalMinutes { get; set; } = 120;
    public int DelayMilliseconds { get; set; } = 3000;
    public int BatchSize { get; set; } = 25;
    public int BatchPauseSeconds { get; set; } = 30;
    public int MaximumMinutes { get; set; } = 60;
    public int StartMinute { get; set; }
    public int EndMinute { get; set; } = 1440;
    public string TimeZone { get; set; } = TimeZoneInfo.Local.Id;
    public int ConsecutiveErrorLimit { get; set; } = 3;
    public LibraryCollectionOptions Copy() => (LibraryCollectionOptions)MemberwiseClone();
    public void Validate()
    {
        if (Version != 1 || IntervalMinutes is < 5 or > 10080 || DelayMilliseconds is < 1000 or > 60000
            || BatchSize is < 1 or > 500 || BatchPauseSeconds is < 0 or > 3600 || MaximumMinutes is < 1 or > 1440
            || StartMinute is < 0 or > 1439 || EndMinute is < 1 or > 1440 || StartMinute == EndMinute
            || ConsecutiveErrorLimit is < 1 or > 10 || TimeZone is null || TimeZone.Length > 200)
            throw new InvalidDataException("Réglages de collecte invalides. Aucun rythme illimité ou parallèle n’est autorisé.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidDataException("Le fuseau de la plage de collecte n’est pas reconnu."); }
    }
    public bool Allows(DateTimeOffset utc)
    {
        Validate(); var local = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(TimeZone));
        var minute = local.Hour * 60 + local.Minute;
        return StartMinute < EndMinute ? minute >= StartMinute && minute < EndMinute : minute >= StartMinute || minute < EndMinute;
    }
}

public sealed class LibraryCollectionState
{
    public long Revision { get; set; }
    public LibraryCollectionOptions Options { get; set; } = new();
    public string State { get; set; } = "idle";
    public string RunId { get; set; } = "";
    public DateTimeOffset? NextAt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public bool Held => State is "paused" or "stopped" or "interrupted" or "attention";
}

/// <summary>An explicit acquisition scope. Manual consultation is not delayed by collection policy.</summary>
public sealed class LibraryCollectionPacer : IDisposable
{
    private static readonly AsyncLocal<LibraryCollectionPacer?> Current = new();
    private readonly LibraryCollectionPacer? _previous;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LibraryCollectionOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Action<int, TimeSpan>? _progress;
    private readonly Func<CancellationToken, Task>? _verify;
    private TimeSpan _lastStart;
    private int _requests;
    private bool _hasStarted;
    private bool _disposed;
    private LibraryCollectionPacer(LibraryCollectionOptions options, Action<int, TimeSpan>? progress, Func<CancellationToken, Task>? verify)
    { _options = options.Copy(); _options.Validate(); _progress = progress; _verify = verify; _previous = Current.Value; Current.Value = this; }
    public static LibraryCollectionPacer Enter(LibraryCollectionOptions options, Action<int, TimeSpan>? progress = null, Func<CancellationToken, Task>? verify = null) => new(options, progress, verify);
    public static Task BeforeNavigationAsync(CancellationToken ct) => Current.Value?.WaitAsync(ct) ?? Task.CompletedTask;
    private async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); ct.ThrowIfCancellationRequested();
            var gap = TimeSpan.FromMilliseconds(_options.DelayMilliseconds);
            if (_requests > 0 && _requests % _options.BatchSize == 0) gap += TimeSpan.FromSeconds(_options.BatchPauseSeconds);
            var delay = _hasStarted ? gap - (_clock.Elapsed - _lastStart) : TimeSpan.Zero;
            if (delay > TimeSpan.Zero) { _progress?.Invoke(_requests, delay); await Task.Delay(delay, ct).ConfigureAwait(false); }
            if (_verify is not null) await _verify(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); _lastStart = _clock.Elapsed; _hasStarted = true; _requests++;
            _progress?.Invoke(_requests, TimeSpan.Zero);
        }
        finally { _gate.Release(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Current.Value = _previous;
        // Waiters are cancelled by their owning capture before the scope is disposed.
    }
}
