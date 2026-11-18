using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

public sealed class NativeColumnInspection
{
    [JsonPropertyName("supported")] public bool Supported { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("proof")] public string Proof { get; set; } = "";
    [JsonPropertyName("tableSelector")] public string TableSelector { get; set; } = "";
    [JsonPropertyName("openerSelector")] public string OpenerSelector { get; set; } = "";
    [JsonPropertyName("modalSelector")] public string ModalSelector { get; set; } = "";
    [JsonPropertyName("iframeSelector")] public string IframeSelector { get; set; } = "";
    [JsonPropertyName("closeSelector")] public string CloseSelector { get; set; } = "";
}

public sealed class NativeColumnPreferences
{
    [JsonPropertyName("supported")] public bool Supported { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("proof")] public string Proof { get; set; } = "";
    [JsonPropertyName("layout")] public string Layout { get; set; } = "";
    [JsonPropertyName("itemType")] public string ItemType { get; set; } = "";
    [JsonPropertyName("usersId")] public string UsersId { get; set; } = "";
    [JsonPropertyName("personalActive")] public bool PersonalActive { get; set; }
    [JsonPropertyName("personalScope")] public bool PersonalScope { get; set; }
    [JsonPropertyName("personalTabSelector")] public string PersonalTabSelector { get; set; } = "";
    [JsonPropertyName("personalTabSelectSelector")] public string PersonalTabSelectSelector { get; set; } = "";
    [JsonPropertyName("personalTabValue")] public string PersonalTabValue { get; set; } = "";
    [JsonPropertyName("formSelector")] public string FormSelector { get; set; } = "";
    [JsonPropertyName("activateSelector")] public string ActivateSelector { get; set; } = "";
    [JsonPropertyName("selectSelector")] public string SelectSelector { get; set; } = "";
    [JsonPropertyName("selectHidden")] public bool SelectHidden { get; set; }
    [JsonPropertyName("addSelector")] public string AddSelector { get; set; } = "";
    [JsonPropertyName("options")] public List<NativeColumnOption> Options { get; set; } = new();
    [JsonPropertyName("selected")] public List<NativeSelectedColumn> Selected { get; set; } = new();
    [JsonPropertyName("selectedIdsKnown")] public bool SelectedIdsKnown { get; set; }
}

public sealed class NativeColumnOption
{
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("disabled")] public bool Disabled { get; set; }
}

public sealed class NativeSelectedColumn
{
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("preferenceId")] public string PreferenceId { get; set; } = "";
    [JsonPropertyName("fixed")] public bool Fixed { get; set; }
}

public sealed record ColumnPreparationResult(TableChoice Table, ColumnPreparationReceipt Receipt);
