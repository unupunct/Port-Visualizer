using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PortVisualizer.Core.Models;

namespace PortVisualizer.Core.Export;

public enum ExportFormat { Csv, Json, Html }

/// <summary>A generic tabular export so every data set shares the same CSV/JSON/HTML writers.</summary>
public sealed class ExportTable
{
    public ExportTable(string title, IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows,
        DateTime? fromUtc = null, DateTime? toUtc = null, DateTime? exportedUtc = null)
    {
        Title = title; Columns = columns; Rows = rows; FromUtc = fromUtc; ToUtc = toUtc;
        ExportedUtc = exportedUtc ?? DateTime.UtcNow;
    }

    public string Title { get; }
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<string?[]> Rows { get; }
    public DateTime? FromUtc { get; }
    public DateTime? ToUtc { get; }
    public DateTime ExportedUtc { get; }
}

public static class ExportBuilder
{
    private static readonly string[] ConnectionColumns =
        { "Process", "PID", "ExecutablePath", "Protocol", "LocalAddress", "LocalPort", "RemoteAddress", "RemotePort", "State", "FirstObservedUtc", "LastObservedUtc" };

    public static string Iso(DateTime? d) => d?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) ?? "";

    public static ExportTable Connections(string title, IEnumerable<TrackedConnection> items, DateTime? exportedUtc = null)
    {
        var rows = items.Select(t =>
        {
            var c = t.Connection;
            return new string?[]
            {
                c.Process.Name, c.Process.Pid.ToString(), c.Process.Path, c.Protocol.ToString().ToUpperInvariant(),
                c.LocalAddress, c.LocalPort.ToString(), c.RemoteAddress, c.RemoteAddress is null ? "" : c.RemotePort.ToString(),
                c.State.ToDisplay(), Iso(t.FirstObservedUtc), Iso(t.LastObservedUtc),
            };
        }).ToList();
        return new ExportTable(title, ConnectionColumns, rows, exportedUtc: exportedUtc);
    }

    public static ExportTable History(IEnumerable<ConnectionEvent> events, DateTime? from, DateTime? to, DateTime? exportedUtc = null)
    {
        var cols = new[] { "TimestampUtc", "EventType", "Process", "PID", "ExecutablePath", "Protocol", "LocalAddress", "LocalPort", "RemoteAddress", "RemotePort", "State" };
        var rows = events.Select(e => new string?[]
        {
            Iso(e.TimestampUtc), e.Type.ToString(), e.ProcessName, e.Pid.ToString(), e.ProcessPath, e.Protocol.ToString().ToUpperInvariant(),
            e.LocalAddress, e.LocalPort.ToString(), e.RemoteAddress, e.RemoteAddress is null ? "" : e.RemotePort.ToString(), e.State.ToDisplay(),
        }).ToList();
        return new ExportTable("Connection history", cols, rows, from, to, exportedUtc);
    }

    public static ExportTable ProcessSummary(IEnumerable<TrackedConnection> items, DateTime? exportedUtc = null)
    {
        var cols = new[] { "Process", "PID", "ExecutablePath", "Connections", "TcpConnections", "UdpEndpoints", "ListeningPorts", "RemoteIps", "FirstObservedUtc", "LastObservedUtc" };
        var rows = items.GroupBy(t => t.Connection.Process.Key).Select(g =>
        {
            var p = g.First().Connection.Process;
            return new string?[]
            {
                p.Name, p.Pid.ToString(), p.Path, g.Count().ToString(),
                g.Count(x => x.Connection.Protocol == Protocol.Tcp && !x.Connection.IsListening).ToString(),
                g.Count(x => x.Connection.IsUdp).ToString(), g.Count(x => x.Connection.IsListening).ToString(),
                g.Where(x => x.Connection.HasRemote).Select(x => x.Connection.RemoteAddress).Distinct().Count().ToString(),
                Iso(g.Min(x => x.FirstObservedUtc)), Iso(g.Max(x => x.LastObservedUtc)),
            };
        }).OrderByDescending(r => int.Parse(r[3]!)).ToList();
        return new ExportTable("Process connection summary", cols, rows, exportedUtc: exportedUtc);
    }
}

public static class Exporter
{
    public static string Render(ExportTable t, ExportFormat f) => f switch
    {
        ExportFormat.Csv => ToCsv(t),
        ExportFormat.Json => ToJson(t),
        _ => ToHtml(t),
    };

    public static string Extension(ExportFormat f) => f switch { ExportFormat.Csv => ".csv", ExportFormat.Json => ".json", _ => ".html" };

    public static async Task WriteAsync(string path, ExportTable t, ExportFormat f, CancellationToken ct = default) =>
        await File.WriteAllTextAsync(path, Render(t, f), new UTF8Encoding(true), ct).ConfigureAwait(false);

    public static string ToCsv(ExportTable t)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(t.Title).Append(" | exported ").Append(ExportBuilder.Iso(t.ExportedUtc));
        if (t.FromUtc is not null || t.ToUtc is not null)
            sb.Append(" | range ").Append(ExportBuilder.Iso(t.FromUtc)).Append(" .. ").Append(ExportBuilder.Iso(t.ToUtc));
        sb.Append("\r\n");
        sb.AppendJoin(',', t.Columns.Select(Csv)).Append("\r\n");
        foreach (var r in t.Rows) sb.AppendJoin(',', r.Select(Csv)).Append("\r\n");
        return sb.ToString();
    }

    private static string Csv(string? v)
    {
        v ??= "";
        // Neutralise spreadsheet formula injection from process names/paths.
        if (v.Length > 0 && v[0] is '=' or '+' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string ToJson(ExportTable t)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("title", t.Title);
            w.WriteString("exportedUtc", ExportBuilder.Iso(t.ExportedUtc));
            if (t.FromUtc is not null) w.WriteString("rangeFromUtc", ExportBuilder.Iso(t.FromUtc));
            if (t.ToUtc is not null) w.WriteString("rangeToUtc", ExportBuilder.Iso(t.ToUtc));
            w.WriteNumber("count", t.Rows.Count);
            w.WriteStartArray("rows");
            foreach (var r in t.Rows)
            {
                w.WriteStartObject();
                for (var i = 0; i < t.Columns.Count; i++)
                {
                    if (r[i] is null) w.WriteNull(t.Columns[i]); else w.WriteString(t.Columns[i], r[i]);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string ToHtml(ExportTable t)
    {
        Func<string?, string> e = s => WebUtility.HtmlEncode(s) ?? "";
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(e(t.Title)).Append("</title><style>")
          .Append("body{font-family:Segoe UI,Arial,sans-serif;background:#14161a;color:#d7dde3;margin:24px}h1{color:#00e5a0;font-weight:600}")
          .Append("table{border-collapse:collapse;width:100%;font-size:13px}th,td{border-bottom:1px solid #2a2f36;padding:6px 10px;text-align:left}")
          .Append("th{background:#0b0c0e;color:#00d4ff}tr:hover td{background:#1c2026}.meta{color:#8a949e;margin-bottom:16px}</style></head><body>");
        sb.Append("<h1>").Append(e(t.Title)).Append("</h1><div class=\"meta\">Exported (UTC): ").Append(e(ExportBuilder.Iso(t.ExportedUtc)));
        if (t.FromUtc is not null || t.ToUtc is not null)
            sb.Append(" &middot; Date range: ").Append(e(ExportBuilder.Iso(t.FromUtc))).Append(" to ").Append(e(ExportBuilder.Iso(t.ToUtc)));
        sb.Append(" &middot; Rows: ").Append(t.Rows.Count).Append(" &middot; Generated by Port Visualizer. Data was observed locally; unknown connections are not necessarily malicious.</div><table><thead><tr>");
        foreach (var c in t.Columns) sb.Append("<th>").Append(e(c)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var r in t.Rows)
        {
            sb.Append("<tr>");
            foreach (var v in r) sb.Append("<td>").Append(e(v ?? "")).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></body></html>");
        return sb.ToString();
    }
}
