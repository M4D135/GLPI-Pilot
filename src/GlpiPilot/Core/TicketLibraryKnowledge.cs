using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GlpiPilot.Core;

/// <summary>
/// Read-only, versioned projections of the existing library documents. This is a
/// corpus contract, not a second store, an authorization service or an AI client.
/// </summary>
public static class TicketLibraryKnowledge
{
    public const int Version = 1;
    public const string SqlitePrefix = "ticket_library_knowledge_v1_";
    public const string PostgresPrefix = "glpi_pilot_library_knowledge_v1_";
    private static readonly string[] ViewSuffixes = ["evidence", "versions", "tickets", "fields", "sections", "headers", "rows", "cells", "items", "tabs", "limitations", "texts"];
    // The order and labels are part of corpus v1. Selectors are provenance, not
    // ticket content; clocks/selectors do not participate in the document hash.
    private static readonly string[] ItemProperties = ["kind", "identity", "sourceSection", "title", "date", "author", "authorRole", "owner", "status", "due", "plannedStart", "plannedEnd", "completedAt", "requestedAt", "waitingStart", "waitingEnd", "waitingReason", "url", "relatedType"];
    public static IReadOnlyList<string> PostgresViewNames { get; } = Array.AsReadOnly(ViewSuffixes.Select(s => PostgresPrefix + s).ToArray());
    public static string SqliteSchemaSql { get; } = BuildSchema(postgres: false);
    public static string PostgresSchemaSql { get; } = BuildSchema(postgres: true);

    internal static IEnumerable<TicketLibraryCitation> SupplementalChunks(TicketLibraryDocument document)
    {
        IEnumerable<TicketLibraryCitation> Entry(string path, string section, string sectionKey, int row, string text)
        {
            var part = 0;
            foreach (var excerpt in TicketLibrarySearch.Parts(text))
            {
                yield return new()
                {
                    ChunkKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.Key + "\n" + document.ContentHash
                        + "\nknowledge-v1\n" + path + "\n" + (part++).ToString(CultureInfo.InvariantCulture)))).ToLowerInvariant(),
                    Key = document.Key, Ticket = document.Seed.Ticket, Section = section, SectionKey = sectionKey, Row = row,
                    Excerpt = excerpt, Url = document.Seed.Ticket.Url, ObservedAt = document.ReadAt ?? document.Seed.ObservedAt,
                    Current = TicketLibraryRules.IsContentConfirmed(document), ContentHash = document.ContentHash
                };
            }
        }

        foreach (var field in document.Seed.Fields.OrderBy(p => p.Key, StringComparer.Ordinal))
            foreach (var chunk in Entry("/seed/fields/" + Pointer(field.Key), "Champs de l’index GLPI", "index", -1, field.Key + ": " + field.Value)) yield return chunk;
        for (var column = 0; column < document.Seed.IndexValues.Count; column++)
            foreach (var chunk in Entry("/seed/indexValues/" + column.ToString(CultureInfo.InvariantCulture), "Colonnes de l’index GLPI", "index", column,
                document.Seed.IndexHeaders[column] + ": " + document.Seed.IndexValues[column])) yield return chunk;
        foreach (var chunk in Entry("/seed/status", "Statut GLPI", "index", -1, document.Seed.Status)) yield return chunk;
        if (document.Detail is not { } detail) yield break;
        foreach (var chunk in Entry("/detail/title", "Titre de la fiche GLPI", "detail", -1, detail.Title)) yield return chunk;
        for (var sectionIndex = 0; sectionIndex < detail.Sections.Count; sectionIndex++)
        {
            var section = detail.Sections[sectionIndex];
            for (var rowIndex = 0; rowIndex < section.Data.Rows.Count; rowIndex++)
            {
                var row = section.Data.Rows[rowIndex];
                // Existing solution chunks intentionally contain only their body.
                // Keep their keys/body unchanged and index their metadata separately.
                if (!(document.Seed.StatusCode is 5 or 6 && document.DetailsCurrent && document.DetailComplete
                    && section.Kind == "timeline" && row.Length > 3 && string.Equals(row[0], "Solution", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(row[3]))) continue;
                var text = string.Join("\n", row.Select((value, column) => column == 3 || string.IsNullOrWhiteSpace(value) ? ""
                    : column < section.Data.Headers.Count ? section.Data.Headers[column] + ": " + value : value).Where(value => value.Length > 0));
                foreach (var chunk in Entry("/detail/sections/" + sectionIndex.ToString(CultureInfo.InvariantCulture) + "/data/rows/"
                    + rowIndex.ToString(CultureInfo.InvariantCulture) + "/metadata", section.Name, section.TabKey, rowIndex, text)) yield return chunk;
            }
        }
        for (var itemIndex = 0; itemIndex < detail.ObservedItems.Count; itemIndex++)
        {
            var item = detail.ObservedItems[itemIndex];
            string?[] values = [item.Kind, item.Identity, item.SourceSection, item.Title, item.Date, item.Author, item.AuthorRole, item.Owner, item.Status,
                item.Due, item.PlannedStart, item.PlannedEnd, item.CompletedAt, item.RequestedAt, item.WaitingStart, item.WaitingEnd, item.WaitingReason, item.Url, item.RelatedType];
            var text = string.Join("\n", values.Select((value, index) => string.IsNullOrWhiteSpace(value) ? "" : ItemProperties[index] + ": " + value).Where(value => value.Length > 0));
            foreach (var chunk in Entry("/detail/observedItems/" + itemIndex.ToString(CultureInfo.InvariantCulture),
                item.SourceSection.Length > 0 ? item.SourceSection : "Éléments observés", item.SourceTabKey, itemIndex, text)) yield return chunk;
        }
    }

    private static string Pointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string BuildSchema(bool postgres)
    {
        var p = postgres ? PostgresPrefix : SqlitePrefix;
        var native = postgres ? "glpi_pilot_library_" : "ticket_library_";
        var sql = new StringBuilder();
        if (postgres) sql.AppendLine("CREATE INDEX IF NOT EXISTS glpi_pilot_library_knowledge_conflict_key ON glpi_pilot_library_conflicts(ticket_key);");
        string Text(string json, params string[] path) => postgres
            ? "(" + json + "::jsonb #>> '{" + string.Join(',', path) + "}')"
            : "json_extract(" + json + ",'$." + string.Join('.', path) + "')";
        string Json(string json, params string[] path) => postgres
            ? "(" + json + "::jsonb #> '{" + string.Join(',', path) + "}')"
            : "json_extract(" + json + ",'$." + string.Join('.', path) + "')";
        string Flag(string json, params string[] path) => postgres
            ? "CASE WHEN " + Text(json, path) + "='true' THEN 1 ELSE 0 END"
            : "COALESCE(" + Text(json, path) + ",0)";
        string Array(string expression, string alias) => postgres
            ? "CROSS JOIN LATERAL jsonb_array_elements(COALESCE(NULLIF(" + expression + ",'null'::jsonb),'[]'::jsonb)) WITH ORDINALITY AS " + alias + "(value,ordinal)"
            : "JOIN json_each(" + expression + ") " + alias;
        string Object(string expression, string alias) => postgres
            ? "CROSS JOIN LATERAL jsonb_each_text(COALESCE(NULLIF(" + expression + ",'null'::jsonb),'{}'::jsonb)) AS " + alias + "(key,value)"
            : "JOIN json_each(" + expression + ") " + alias;
        string Ordinal(string alias) => postgres ? "(" + alias + ".ordinal-1)" : "CAST(" + alias + ".key AS INTEGER)";
        string Scalar(string alias) => postgres ? "(" + alias + ".value #>> '{}')" : alias + ".value";
        string At(string json, string array, string index) => postgres
            ? "(" + Json(json, array) + " ->> CAST(" + index + " AS INTEGER))"
            : "json_extract(" + json + ",'$." + array + "[' || " + index + " || ']')";
        string Size(string expression) => postgres ? "jsonb_array_length(COALESCE(NULLIF(" + expression + ",'null'::jsonb),'[]'::jsonb))" : "COALESCE(json_array_length(" + expression + "),0)";
        var newline = postgres ? "chr(10)" : "char(10)";
        var carriage = postgres ? "chr(13)" : "char(13)";
        string Lines(string select) => postgres
            ? "(SELECT COALESCE(string_agg(line," + newline + " ORDER BY ordinal),'') FROM (" + select + ") AS lines WHERE line IS NOT NULL AND line<>'')"
            : "(SELECT COALESCE(group_concat(line," + newline + "),'') FROM (" + select + " ORDER BY ordinal) AS lines WHERE line IS NOT NULL AND line<>'')";
        void View(string name, string select) => sql.Append(postgres ? "CREATE OR REPLACE VIEW " : "CREATE VIEW IF NOT EXISTS ").Append(p).Append(name).Append(" AS\n").Append(select).Append(";\n");
        string Identity(string alias) => alias + ".ticket_key," + alias + ".version_id," + alias + ".version_kind";
        const string doc = "e.document_json";
        var localRevision = postgres ? "CAST(COALESCE(" + Text("t.document_json", "localRevision") + ",'0') AS BIGINT)" : "t.local_revision";
        var centralRevision = postgres ? "t.revision" : "t.central_revision";
        var historyId = postgres ? "h.revision" : "h.history_id";
        var historyDate = postgres ? "CAST(NULL AS TEXT)" : "h.recorded_at";
        View("evidence", $"""
            SELECT t.ticket_key,'selected' AS version_id,'selected' AS version_kind,t.document_json,t.content_hash,
              {localRevision} AS local_revision,{centralRevision} AS central_revision,CAST(NULL AS TEXT) AS recorded_at,'' AS reason
              FROM {native}tickets t
            UNION ALL SELECT h.ticket_key,'history:' || CAST({historyId} AS TEXT),'history',h.document_json,{Text("h.document_json", "contentHash")},
              CAST(COALESCE({Text("h.document_json", "localRevision")},'0') AS BIGINT),CAST(COALESCE({Text("h.document_json", "centralRevision")},'0') AS BIGINT),{historyDate},h.reason FROM {native}history h
            UNION ALL SELECT c.ticket_key,'conflict:' || c.conflict_id || ':current','conflict-current',c.current_json,{Text("c.current_json", "contentHash")},
              CAST(COALESCE({Text("c.current_json", "localRevision")},'0') AS BIGINT),CAST(COALESCE({Text("c.current_json", "centralRevision")},'0') AS BIGINT),CAST(c.recorded_at AS TEXT),c.reason FROM {native}conflicts c
            UNION ALL SELECT c.ticket_key,'conflict:' || c.conflict_id || ':incoming','conflict-incoming',c.incoming_json,{Text("c.incoming_json", "contentHash")},
              CAST(COALESCE({Text("c.incoming_json", "localRevision")},'0') AS BIGINT),CAST(COALESCE({Text("c.incoming_json", "centralRevision")},'0') AS BIGINT),CAST(c.recorded_at AS TEXT),c.reason FROM {native}conflicts c
            """);
        var confirmationId = Text(doc, "confirmationId");
        var validConfirmationId = postgres ? confirmationId + " ~ '^[0-9A-Fa-f]{32}$'"
            : "length(" + confirmationId + ")=32 AND " + confirmationId + " NOT GLOB '*[^0-9A-Fa-f]*'";
        var hasContent = postgres ? "(" + Json(doc, "detail") + " IS NOT NULL AND " + Json(doc, "detail") + "<>'null'::jsonb)"
            : "json_type(" + doc + ",'$.detail')='object'";
        var confirmed = "e.version_kind='selected' AND " + hasContent + " AND " + Flag(doc, "detailsCurrent") + "=1 AND " + Flag(doc, "confirmationPending") + "=0 AND length(e.content_hash)=64 AND "
            + Text(doc, "confirmedContentHash") + "=e.content_hash AND " + validConfirmationId;
        View("versions", $"""
            SELECT {Identity("e")},{Text(doc, "installation")} AS installation,{Text(doc, "seed", "ticket", "id")} AS ticket_id,
              {Text(doc, "seed", "ticket", "url")} AS ticket_url,{Text(doc, "seed", "title")} AS title,{Text(doc, "detail", "title")} AS detail_title,
              {Text(doc, "seed", "status")} AS status,CAST({Text(doc, "seed", "statusCode")} AS INTEGER) AS status_code,{Text(doc, "seed", "category")} AS category,
              {Text(doc, "seed", "lastModifiedRaw")} AS last_modified_raw,{Text(doc, "seed", "lastModifiedAt")} AS last_modified_at,
              {Flag(doc, "seed", "lastModifiedReliable")} AS last_modified_reliable,{Text(doc, "seed", "lastModifiedPrecision")} AS last_modified_precision,
              {Text(doc, "seed", "observedAt")} AS observed_at,{Text(doc, "readAt")} AS read_at,{Text(doc, "detailModifiedRaw")} AS detail_modified_raw,{Text(doc, "detailModifiedAt")} AS detail_modified_at,
              e.content_hash,e.local_revision,e.central_revision,e.recorded_at,e.reason,{Text(doc, "readerSignature")} AS reader_signature,
              {Flag(doc, "detailComplete")} AS detail_complete,CASE WHEN {confirmed} THEN 1 ELSE 0 END AS content_confirmed,
              {Flag(doc, "confirmationPending")} AS confirmation_pending,CASE WHEN {hasContent} THEN 1 ELSE 0 END AS has_content,
              CAST({Text(doc, "coverage", "tabsDiscovered")} AS INTEGER) AS tabs_discovered,CAST({Text(doc, "coverage", "tabsLoaded")} AS INTEGER) AS tabs_loaded,
              {Flag(doc, "coverage", "complete")} AS coverage_complete,
              (SELECT COUNT(*) FROM {native}conflicts c WHERE c.ticket_key=e.ticket_key) AS conflict_count,
              'collecting-session' AS access_basis,'unknown' AS visibility
            FROM {p}evidence e
            """);
        View("tickets", $"SELECT * FROM {p}versions WHERE version_kind='selected'");
        var fieldPath = "'/seed/fields/' || replace(replace(f.key,'~','~0'),'/','~1')";
        View("fields", $"""
            SELECT {Identity("e")},'seed-field' AS origin,{fieldPath} AS source_path,-1 AS column_index,f.key AS name,f.value AS value
              FROM {p}evidence e {Object(Json(doc, "seed", "fields"), "f")}
            UNION ALL SELECT {Identity("e")},'index-column','/seed/indexValues/' || CAST({Ordinal("a")} AS TEXT),{Ordinal("a")},
              {At(Json(doc, "seed"), "indexHeaders", Ordinal("a"))},{Scalar("a")}
              FROM {p}evidence e {Array(Json(doc, "seed", "indexValues"), "a")}
            """);
        View("sections", $"""
            SELECT {Identity("e")},{Ordinal("s")} AS section_index,'/detail/sections/' || CAST({Ordinal("s")} AS TEXT) AS source_path,
              {Text("s.value", "name")} AS name,{Text("s.value", "kind")} AS kind,{Text("s.value", "tabKey")} AS tab_key,{Text("s.value", "tabName")} AS tab_name,
              {Text("s.value", "selector")} AS selector,{Text("s.value", "tabSelector")} AS tab_selector,{Text("s.value", "data", "sourceUrl")} AS source_url,
              {Text("s.value", "data", "title")} AS title,{Text("s.value", "data", "extractedAt")} AS extracted_at,{Text("s.value", "data", "scope")} AS scope,
              {Flag("s.value", "data", "complete")} AS complete,CAST({Text("s.value", "data", "expectedTotal")} AS BIGINT) AS expected_rows,
              {Size(Json("s.value", "data", "rows"))} AS row_count,{Size(Json("s.value", "data", "headers"))} AS column_count,
              {Json("s.value", "data", "quality")} AS quality_json,{Json("s.value", "data", "provenance")} AS provenance_json
              FROM {p}evidence e {Array(Json(doc, "detail", "sections"), "s")}
            """);
        View("headers", $"""
            SELECT {Identity("e")},{Ordinal("s")} AS section_index,{Ordinal("h")} AS column_index,{Scalar("h")} AS name
              FROM {p}evidence e {Array(Json(doc, "detail", "sections"), "s")} {Array(Json("s.value", "data", "headers"), "h")}
            """);
        var rowHeader = At(Json("s.value", "data"), "headers", Ordinal("c"));
        var cellBody = "CASE WHEN " + Scalar("c") + "='' THEN '' WHEN COALESCE(" + rowHeader + ",'')='' THEN " + Scalar("c") + " ELSE " + rowHeader + " || ': ' || " + Scalar("c") + " END";
        var rowText = Lines($"SELECT {Ordinal("c")} AS ordinal,{cellBody} AS line FROM {(postgres ? "(SELECT 1) AS anchor " : "(SELECT 1) ")}{Array("r.value", "c")}");
        var metadata = postgres ? "(r.value->>4)" : "json_extract(r.value,'$[4]')";
        var privateLine = newline + " || lower(replace(COALESCE(" + metadata + ",'')," + carriage + ",'')) || " + newline;
        var privateNeedle = newline + " || 'privé' || " + newline;
        var privateMatch = postgres ? "strpos(" + privateLine + "," + privateNeedle + ")>0" : "instr(" + privateLine + "," + privateNeedle + ")>0";
        var privacy = "CASE WHEN " + Text("s.value", "kind") + "='timeline' AND lower(" + At(Json("s.value", "data"), "headers", "4") + ")='informations' AND " + privateMatch + " THEN 'private' ELSE 'unknown' END";
        View("rows", $"""
            SELECT {Identity("e")},{Ordinal("s")} AS section_index,{Ordinal("r")} AS row_index,
              '/detail/sections/' || CAST({Ordinal("s")} AS TEXT) || '/data/rows/' || CAST({Ordinal("r")} AS TEXT) AS source_path,
              {Text("s.value", "name")} AS section,{Text("s.value", "tabKey")} AS section_key,{Text("s.value", "kind")} AS kind,
              {Text("s.value", "data", "sourceUrl")} AS source_url,{Text("s.value", "data", "extractedAt")} AS observed_at,
              {Flag("s.value", "data", "complete")} AS complete,{Size("r.value")} AS cell_count,{privacy} AS visibility,{rowText} AS body
              FROM {p}evidence e {Array(Json(doc, "detail", "sections"), "s")} {Array(Json("s.value", "data", "rows"), "r")}
            """);
        View("cells", $"""
            SELECT {Identity("e")},{Ordinal("s")} AS section_index,{Ordinal("r")} AS row_index,{Ordinal("c")} AS column_index,
              {rowHeader} AS name,{Scalar("c")} AS value
              FROM {p}evidence e {Array(Json(doc, "detail", "sections"), "s")} {Array(Json("s.value", "data", "rows"), "r")} {Array("r.value", "c")}
            """);
        // Explicit scalar columns are intentionally available even when no text
        // has been received. Empty values remain empty; absent values stay NULL.
        string Snake(string name) => string.Concat(name.Select(c => char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : c.ToString()));
        var itemColumns = string.Join(",\n  ", ItemProperties.Select(name => Text("i.value", name) + " AS " + Snake(name)));
        var itemText = Lines(string.Join(" UNION ALL ", ItemProperties.Select((name, index) => "SELECT " + index.ToString(CultureInfo.InvariantCulture) + " AS ordinal,CASE WHEN COALESCE("
            + Text("i.value", name) + ",'')='' THEN '' ELSE '" + name + ": ' || " + Text("i.value", name) + " END AS line")));
        View("items", $"""
            SELECT {Identity("e")},{Ordinal("i")} AS item_index,'/detail/observedItems/' || CAST({Ordinal("i")} AS TEXT) AS source_path,
              {Text("i.value", "observedAt")} AS observed_at,{Text("i.value", "sourceTabKey")} AS source_tab_key,{Text("i.value", "sourceTabSelector")} AS source_tab_selector,
              {Text("i.value", "sourceSelector")} AS source_selector,{itemColumns},'unknown' AS visibility,{itemText} AS body
              FROM {p}evidence e {Array(Json(doc, "detail", "observedItems"), "i")}
            """);
        View("tabs", $"""
            SELECT {Identity("e")},{Ordinal("a")} AS tab_index,{Text("a.value", "key")} AS tab_key,{Text("a.value", "name")} AS name,
              {Text("a.value", "category")} AS category,{Text("a.value", "selector")} AS selector,{Flag("a.value", "isActive")} AS is_active,{Flag("a.value", "loaded")} AS loaded
              FROM {p}evidence e {Array(Json(doc, "detail", "tabs"), "a")}
            """);
        View("limitations", $"""
            SELECT {Identity("e")},'coverage' AS origin,-1 AS section_index,{Ordinal("a")} AS ordinal,{Scalar("a")} AS body
              FROM {p}evidence e {Array(Json(doc, "coverage", "limitations"), "a")}
            UNION ALL SELECT {Identity("e")},'detail-warning',-1,{Ordinal("a")},{Scalar("a")}
              FROM {p}evidence e {Array(Json(doc, "detail", "warnings"), "a")}
            UNION ALL SELECT {Identity("e")},'section-warning',{Ordinal("s")},{Ordinal("a")},{Scalar("a")}
              FROM {p}evidence e {Array(Json(doc, "detail", "sections"), "s")} {Array(Json("s.value", "data", "warnings"), "a")}
            """);
        View("texts", $"""
            SELECT v.*,x.source_path,x.source_kind,x.section,x.section_key,x.row_index,x.source_url,x.source_observed_at,x.text_visibility,x.body,
              v.ticket_key || '@' || v.content_hash || '/' || v.version_id || x.source_path AS citation_key
            FROM {p}versions v JOIN (
              SELECT {Identity("v")},'/seed' AS source_path,'index-summary' AS source_kind,'Index GLPI' AS section,'index' AS section_key,-1 AS row_index,
                v.ticket_url AS source_url,v.observed_at AS source_observed_at,'unknown' AS text_visibility,
                COALESCE(v.ticket_id,'') || {newline} || COALESCE(v.title,'') || {newline} || COALESCE(v.category,'') || {newline} || COALESCE(v.status,'') AS body FROM {p}versions v
              UNION ALL SELECT {Identity("f")},f.source_path,f.origin,'Index GLPI','index',f.column_index,NULL,NULL,'unknown',COALESCE(f.name || ': ','') || COALESCE(f.value,'') FROM {p}fields f
              UNION ALL SELECT {Identity("v")},'/detail/title','detail-title','Titre de la fiche GLPI','detail',-1,v.ticket_url,v.read_at,'unknown',v.detail_title FROM {p}versions v WHERE v.has_content=1
              UNION ALL SELECT {Identity("r")},r.source_path,'section-row',r.section,r.section_key,r.row_index,r.source_url,r.observed_at,r.visibility,r.body FROM {p}rows r
              UNION ALL SELECT {Identity("i")},i.source_path,'observed-item',i.source_section,i.source_tab_key,i.item_index,i.url,i.observed_at,i.visibility,i.body FROM {p}items i
            ) x ON x.ticket_key=v.ticket_key AND x.version_id=v.version_id
            """);
        return sql.ToString();
    }
}
