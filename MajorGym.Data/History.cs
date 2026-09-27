using System.Text.Json;
using System.Text.Json.Nodes;

namespace MajorGym.Data;

/// <summary>Ported 1:1 from Android's <c>HistoryEntry</c> data class. Field names below are
/// the C# property names; the JSON keys they (de)serialize to/from are the original
/// Android JSON keys ("type", "plan", "fee", "date", "expiry") — see <see cref="History"/>
/// — and must not change, since this JSON is embedded verbatim in <c>Member.HistoryJson</c>
/// and round-trips through the Android-compatible backup file.</summary>
public sealed record HistoryEntry(string Type, string Plan, double Fee, long DateMillis, long ExpiryMillis);

/// <summary>
/// Ported from Android's <c>History.kt</c> (<c>List&lt;HistoryEntry&gt;.toJson()</c> /
/// <c>String.toHistoryList()</c> extension functions). JSON field names ("type", "plan",
/// "fee", "date", "expiry") are preserved exactly.
/// </summary>
public static class History
{
    public static string ToJson(IEnumerable<HistoryEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            var o = new JsonObject
            {
                ["type"] = e.Type,
                ["plan"] = e.Plan,
                ["fee"] = e.Fee,
                ["date"] = e.DateMillis,
                ["expiry"] = e.ExpiryMillis
            };
            arr.Add(o);
        }
        return arr.ToJsonString();
    }

    /// <summary>
    /// Parses stored/synced/restored history JSON defensively: malformed JSON, a non-array
    /// payload, missing fields, wrong types, or one corrupted entry among many good ones
    /// must never crash the Profile screen. A single bad entry is skipped (not silently
    /// pretended never to have existed at the top level — the owner can still tell
    /// something was off by seeing fewer history rows than expected), and a completely
    /// unparsable payload falls back to an empty list rather than throwing. (Android doc
    /// comment, preserved — this defensive behavior is a deliberate fix, not incidental.)
    /// </summary>
    public static List<HistoryEntry> ToHistoryList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<HistoryEntry>();

        JsonArray? arr;
        try
        {
            arr = JsonNode.Parse(json) as JsonArray;
            if (arr is null) return new List<HistoryEntry>();
        }
        catch (JsonException)
        {
            // Corrupted history JSON — show empty history rather than crashing (Android parity).
            return new List<HistoryEntry>();
        }

        var result = new List<HistoryEntry>();
        foreach (var node in arr)
        {
            try
            {
                if (node is not JsonObject o) continue;
                var type = (string?)o["type"] ?? "";
                if (string.IsNullOrWhiteSpace(type)) type = "Unknown";
                var plan = (string?)o["plan"] ?? "";
                if (string.IsNullOrWhiteSpace(plan)) plan = "Unknown";
                var fee = (double?)o["fee"] ?? 0.0;
                if (double.IsNaN(fee) || double.IsInfinity(fee)) fee = 0.0;
                var date = (long?)o["date"] ?? 0L;
                var expiry = (long?)o["expiry"] ?? 0L;
                result.Add(new HistoryEntry(type, plan, fee, date, expiry));
            }
            catch
            {
                // Skip this single corrupted entry, keep the rest — Android parity.
            }
        }
        return result;
    }
}
