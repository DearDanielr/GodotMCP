using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace GodotMcp.Shared;

/// "Did you mean" helpers — when a lookup fails, hand the AI the 3 nearest
/// real candidates so it can self-correct without another round trip.
internal static class Suggestions
{
    public static JsonArray Nearest(string needle, IEnumerable<string> haystack, int top = 3)
    {
        var ranked = new List<(string s, int d)>();
        foreach (var h in haystack)
            ranked.Add((h, Distance(needle, h)));
        ranked.Sort((a, b) => a.d.CompareTo(b.d));

        var arr = new JsonArray();
        int n = 0;
        foreach (var (s, _) in ranked)
        {
            if (n++ >= top) break;
            arr.Add(s);
        }
        return arr;
    }

    /// Case-insensitive Levenshtein. O(len(a)*len(b)); fine for our 100s-of-items lookups.
    private static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
