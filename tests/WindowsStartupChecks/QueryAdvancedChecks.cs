using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;
using GlpiPilot.UI;

namespace GlpiPilot.WindowsStartupChecks;

internal static partial class Program
{
    private static async Task ExerciseAdvancedQueryWorkspaceAsync(TabControl tabs)
    {
        _stage = "advanced query parameters, calculations and row explanations";
        var editor = _queryEditor!; SelectView(tabs, editor);
        var query = new SavedQuery
        {
            Name = "Analyse paramétrée synthétique", Aggregate = "sum", AggregateField = "calc:double", GroupBy = "role:city",
            ConditionExpression = "1 ET 2", Parameters = new() { new() { Key = "ville", Label = "Ville", DefaultValue = "Lyon" } },
            ComputedFields = new() { new() { Key = "double", Label = "Montant doublé", Function = "multiply", Field = "column:N", Constant = "2", Unit = "unités" } },
            Conditions = new()
            {
                new() { Field = "role:city", Operator = "equals", Value = "ville", ValueSource = "parameter" },
                new() { Field = "calc:double", Operator = "number-gt", Value = "0" }
            }
        };
        var data = new ExtractionResult { Headers = new() { "ID", "Ville", "N" }, Rows = new() { new[] { "1", "Lyon", "10" }, new[] { "2", "Marseille", "30" } }, Complete = true };
        QueryLibrarySettings? saved = null;
        void Save(QueryLibrarySettings value) => saved = value;
        editor.SaveRequested += Save;
        try
        {
            editor.LoadSettings(new() { Queries = new() { query } }); editor.Show(data, new()); await FlushLayoutAsync();
            Click(editor, "Prévisualiser"); await editor.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
            var first = AdvancedQueryField<QueryResult>(editor, "_previewResult");
            Require(first.Metric.Value == 20 && first.Groups.Single().Value == 20, "Advanced draft lost calculated fields or numeric grouped values.");
            var outer = AdvancedQueryField<TabControl>(editor, "_sections"); outer.SelectedItem = outer.Items.Cast<TabItem>().Single(tab => tab.Header as string == "Avancé");
            var advanced = AdvancedQueryField<TabControl>(editor, "_advancedSections");
            foreach (var section in advanced.Items.Cast<TabItem>())
            {
                advanced.SelectedItem = section; await FlushLayoutAsync();
                Require(section.Content is FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }, "An advanced query section failed to render: " + section.Header);
            }
            advanced.SelectedIndex = 0; await FlushLayoutAsync();
            var parameters = AdvancedQueryField<DataGrid>(editor, "_parameterGrid");
            var row = parameters.Items[0]; row.GetType().GetProperty("RuntimeValue")!.SetValue(row, "Marseille");
            Require(AdvancedQueryNullableField(editor, "_previewResult") == null, "Changing a runtime parameter retained stale query results.");
            Click(editor, "Prévisualiser"); await editor.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
            Require(AdvancedQueryField<QueryResult>(editor, "_previewResult").Metric.Value == 60, "Runtime parameter was not used by the real query preview.");
            Click(editor, "Enregistrer");
            Require(saved?.Queries.Single().Parameters.Single().DefaultValue == "Lyon", "A preview parameter override leaked into saved defaults.");
            outer.SelectedItem = outer.Items.Cast<TabItem>().Single(tab => tab.Header as string == "Avancé"); advanced.SelectedIndex = 2; await FlushLayoutAsync();
            AdvancedQueryField<TextBox>(editor, "_explainTicket").Text = "2";
            var explain = AdvancedQueryField<Button>(editor, "_explain"); Require(explain.IsEnabled, "Ticket explanation was not enabled.");
            explain.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await editor.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10)); await FlushLayoutAsync();
            var trace = AdvancedQueryField<DataGrid>(editor, "_explainGrid").Items.Cast<QueryConditionExplanation>().ToArray();
            Require(trace.Length == 2 && trace.All(item => item.Decision == "Vrai"), "Real ticket explanation did not apply current parameters and calculations.");
            Pass("Advanced query UI preserves computed fields, previews runtime parameters, saves defaults, renders each editor and explains source tickets");
        }
        finally
        {
            editor.SaveRequested -= Save;
            editor.LoadSettings(Queries);
            var effective = Settings.Copy(); effective.ColumnMappings["city"] = _cities!.CityColumnName;
            editor.Show(_cities.Data, effective, _cities);
        }
    }

    private static T AdvancedQueryField<T>(QueryWorkshopView editor, string name) where T : class
        => AdvancedQueryNullableField(editor, name) as T ?? throw new InvalidOperationException("Query field not available: " + name);
    private static object? AdvancedQueryNullableField(QueryWorkshopView editor, string name)
        => typeof(QueryWorkshopView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(editor);
}
