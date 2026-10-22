using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlpiPilot.Core;

namespace GlpiPilot;

public partial class MainWindow
{
    private DispatcherTimer? _readingPositionTimer;
    private string _readingTicketKey = "", _readingSectionKey = "";
    private bool _restoringReadingPosition;
    private IReadOnlySet<string> _changedTicketRows = new HashSet<string>();

    private void InitializeReadingPersistence()
    {
        _readingPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _readingPositionTimer.Tick += (_, _) =>
        {
            _readingPositionTimer.Stop(); CaptureTicketReadingPosition();
            if (!_closing) SavePersonalWorkspace(_workplace.Copy());
        };
        _ticketWorkplaceView!.ReadingPositionChanged += QueueReadingPositionSave;
        _ticketWorkplaceView.MoveSectionRequested += direction =>
        {
            if (_busy || TicketNavigation.SelectedItem is not TicketNavigationEntry selected) return;
            var entries = TicketNavigation.Items.OfType<TicketNavigationEntry>().Select(e => e.Key).ToList();
            var current = entries.IndexOf(selected.Key); var destination = Math.Clamp(current + Math.Sign(direction), 0, entries.Count - 1);
            if (current < 0 || destination == current) return;
            (entries[current], entries[destination]) = (entries[destination], entries[current]);
            var copy = _workplace.Copy(); copy.SectionOrder = entries;
            if (SavePersonalWorkspace(copy)) BuildTicketNavigation(selected.Key);
        };
        TicketGrid.SelectionChanged += (_, _) => QueueReadingPositionSave();
        TicketGrid.LoadingRow += (_, e) =>
        {
            if (e.Row.Item is DataRowView row && _changedTicketRows.Contains(TicketReadingService.RowFingerprint(row.Row.ItemArray.Select(v => v?.ToString() ?? ""))))
            { e.Row.SetResourceReference(Control.BackgroundProperty, "SelectedBrush"); e.Row.ToolTip = "Ligne nouvelle ou modifiée depuis la lecture précédente."; }
            else { e.Row.ClearValue(Control.BackgroundProperty); e.Row.ClearValue(ToolTipProperty); }
        };
        TicketReaderHost.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => { if (e.VerticalChange != 0) QueueReadingPositionSave(); }));
        Closing += (_, _) => { _readingPositionTimer.Stop(); CaptureTicketReadingPosition(); SavePersonalWorkspace(_workplace.Copy()); };
    }

    private void QueueReadingPositionSave()
    {
        if (_closing || _restoringReadingPosition || _ticketNavigationUpdating || _readingTicketKey.Length == 0 || _readingPositionTimer == null) return;
        _readingPositionTimer.Stop(); _readingPositionTimer.Start();
    }

    private void CaptureTicketReadingPosition()
    {
        if (_restoringReadingPosition || _readingTicketKey.Length == 0) return;
        var record = _workplace.Tickets.FirstOrDefault(t => t.Key == _readingTicketKey);
        if (record == null) return;
        var position = _ticketWorkplaceView?.CapturePosition(record.ReadingPosition) ?? record.ReadingPosition.Copy();
        position.SectionKey = _readingSectionKey; position.Search = TicketSearchBox.Text; position.ViewMode = Math.Clamp(TicketViewModeBox.SelectedIndex, 0, 1);
        position.RowIndex = TicketGrid.SelectedIndex; position.ScrollOffset = _ticketReader?.CaptureOffset() ?? 0;
        record.ReadingPosition = position; record.PreferredSection = _readingSectionKey;
    }

    private TicketReadingPosition? CurrentReadingPosition() => _ticketDetail == null ? null
        : _workplace.Tickets.FirstOrDefault(t => t.Key == WorkplaceStore.TicketKey(_ticketDetail.Ticket))?.ReadingPosition;

    private TicketReadChanges? CurrentReadingChanges() => _ticketDetail == null ? null
        : _personalReadChanges.GetValueOrDefault(WorkplaceStore.TicketKey(_ticketDetail.Ticket));
}
