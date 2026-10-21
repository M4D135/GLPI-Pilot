namespace GlpiPilot.Core;

public sealed class DashboardSettings
{
    public Dictionary<string, string> ColumnMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int StaleDays { get; set; } = 7;
    public int OldDays { get; set; } = 30;
    public int DueSoonHours { get; set; } = 24;
    public string DateCulture { get; set; } = "fr-FR";
    public DashboardLayoutSettings Layout { get; set; } = new();
    private DashboardLayoutSettings? _personalLayout;
    // An older configuration starts with a copy of its existing board. From then
    // on, edits in either workspace cannot replace the other workspace's layout.
    public DashboardLayoutSettings PersonalLayout
    {
        get => _personalLayout ??= Layout?.Copy() ?? new();
        set => _personalLayout = value;
    }
    public string PersonalTechnician { get; set; } = "";
    // Local navigation preference, deliberately excluded from portable configuration.
    public bool OpenPersonalAfterExtraction { get; set; }

    public DashboardSettings Copy()
    {
        var copy = (DashboardSettings)MemberwiseClone();
        copy.ColumnMappings = new(StringComparer.OrdinalIgnoreCase);
        copy.Layout = Layout?.Copy() ?? new();
        copy._personalLayout = _personalLayout?.Copy() ?? copy.Layout.Copy();
        if (ColumnMappings is not null)
            foreach (var pair in ColumnMappings) copy.ColumnMappings[pair.Key] = pair.Value ?? "";
        copy.Normalize();
        return copy;
    }

    internal void Normalize()
    {
        DateCulture ??= "fr-FR";
        PersonalTechnician ??= "";
        Layout ??= new();
        Layout.Normalize();
        _personalLayout ??= Layout.Copy();
        if (ReferenceEquals(_personalLayout, Layout)) _personalLayout = Layout.Copy();
        _personalLayout.Normalize();
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ColumnMappings is not null)
            foreach (var pair in ColumnMappings) mappings[pair.Key] = pair.Value ?? "";
        ColumnMappings = mappings;
    }
}
