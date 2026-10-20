using System.Collections;
using System.Text.Json;
using Keen.Game2.Simulation.GameSystems.BlockDataGenerators;
using Keen.VRage.Library.Filesystem.ContentCache;

namespace VrbReader;

/// <summary>
/// --occupancy: every block model's occupied cells, as the game generated them (GeneratedBlockData metadata blobs in the
/// content cache, contentcache.vrb - what GeneratedBlockDataLoader hands CubeBlockDefinition.SetOccupancy, from which
/// ComputeMassAndHP takes the block's mass). JSON on stdout: { "&lt;model guid&gt;": { "path", "bounds": [min xyz, max xyz],
/// "cells": n, "groups": [[min xyz, max xyz], ...] } } - inclusive 0.25 m cells, the block's local frame.
/// </summary>
static class Occupancy
{
    public static int Dump(string cachePath)
    {
        ContentBlobData data;
        using (var fs = File.OpenRead(cachePath))
            data = ContentCache.DeserializeCC(fs, null);
        var paths = new Dictionary<string, string>();
        foreach (var m in data.Mapping) paths[Key(m.ResourceHandle.ToString())] = m.FileHandle.Path;
        IDictionary? blobs = null;
        foreach (var kv in data.BlobData)
            if (kv.Key == typeof(GeneratedBlockData)) blobs = kv.Value.Data;
        if (blobs == null) { Console.Error.WriteLine($"No GeneratedBlockData blobs in {cachePath}"); return 1; }

        using var w = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = false });
        w.WriteStartObject();
        int n = 0;
        foreach (DictionaryEntry e in blobs)
        {
            var occ = ((GeneratedBlockData)e.Value!).Occupancy;
            if (occ.CellGroups.IsDefaultOrEmpty) continue;
            string key = Key(e.Key.ToString()!);
            w.WriteStartObject(key);
            if (paths.TryGetValue(key, out var p)) w.WriteString("path", p);
            w.WriteStartArray("bounds"); Box(w, occ.Bounds.Min, occ.Bounds.Max); w.WriteEndArray();
            int cells = 0;
            w.WriteStartArray("groups");
            foreach (var g in occ.CellGroups)
            {
                w.WriteStartArray(); Box(w, g.Min, g.Max); w.WriteEndArray();
                cells += g.GetSizeIncludingMax().Volume();
            }
            w.WriteEndArray();
            w.WriteNumber("cells", cells);
            w.WriteEndObject();
            n++;
        }
        w.WriteEndObject();
        w.Flush();
        Console.Error.WriteLine($"Occupancy: {n} block models from {cachePath}");
        return 0;
    }

    static string Key(string handle) => (handle.StartsWith("{G}") ? handle[3..] : handle).ToLowerInvariant();

    static void Box(Utf8JsonWriter w, Keen.VRage.Library.Mathematics.Vector3I lo, Keen.VRage.Library.Mathematics.Vector3I hi)
    {
        w.WriteNumberValue(lo.X); w.WriteNumberValue(lo.Y); w.WriteNumberValue(lo.Z);
        w.WriteNumberValue(hi.X); w.WriteNumberValue(hi.Y); w.WriteNumberValue(hi.Z);
    }
}
