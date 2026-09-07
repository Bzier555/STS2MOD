using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhostDuel.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace GhostDuel.Progression;

/// <summary>
/// PLAN.md M9a: the shared implementation behind both <see cref="GhostSnapshotStore"/> (singleplayer
/// Legacy Ascension) and <see cref="MultiplayerGhostSnapshotStore"/> (the new, deliberately separate
/// multiplayer ladder) — one file-per-level store, keyed only by its own data directory, so the two
/// ladders can never read, overwrite or cap each other. Extracted so the two callers share one
/// implementation rather than a copy-pasted duplicate; every behavior here is unchanged from the
/// original single-ladder <c>GhostSnapshotStore</c> this was split out of (PLAN.md M7), just
/// parameterized by directory instead of hardcoding one.
/// </summary>
internal sealed class GhostSnapshotFileStore
{
    private const int SchemaVersion = 1;

    private readonly string _dataDir;

    public GhostSnapshotFileStore(string dataDirectoryName)
    {
        _dataDir = Path.Combine(OS.GetUserDataDir(), dataDirectoryName);
    }

    private string PathFor(int level) => Path.Combine(_dataDir, $"ghost_a{level}.json");

    public bool Exists(int level) => File.Exists(PathFor(level));

    /// <summary>The highest ascension level currently selectable for this ladder specifically — one
    /// past the highest level with a saved Ghost (0 if none exist yet). See
    /// <see cref="GhostSnapshotStore"/>'s original doc comment for the full rationale; unchanged by
    /// this split other than now being scoped to whichever directory this instance owns.</summary>
    public int GetMaxSelectableLevel()
    {
        int level = 0;
        while (Exists(level))
        {
            level++;
        }
        return level;
    }

    public readonly record struct LoadResult(bool Success, SerializablePlayer? Player, string? Error);

    public LoadResult Load(int level)
    {
        string path = PathFor(level);
        if (!File.Exists(path))
        {
            return new LoadResult(false, null, $"No saved Ghost for ascension {level} at {path}.");
        }
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(path));
            if (root is null)
            {
                return new LoadResult(false, null, $"{path} parsed as null.");
            }
            int version = root["version"]?.GetValue<int>() ?? -1;
            if (version != SchemaVersion)
            {
                return new LoadResult(false, null, $"{path} has schema version {version}, expected {SchemaVersion}.");
            }
            JsonNode? playerNode = root["player"];
            if (playerNode is null)
            {
                return new LoadResult(false, null, $"{path} has no 'player' field.");
            }
            SerializablePlayer? player = playerNode.Deserialize<SerializablePlayer>(JsonSerializationUtility.Options);
            if (player is null)
            {
                return new LoadResult(false, null, $"{path}'s 'player' field parsed as null.");
            }

            List<string> missing = FindMissingIds(player);
            if (missing.Count > 0)
            {
                return new LoadResult(false, null, $"{path} references missing content: {string.Join(", ", missing)}.");
            }
            return new LoadResult(true, player, null);
        }
        catch (Exception ex)
        {
            return new LoadResult(false, null, $"failed to read {path}: {ex.Message}");
        }
    }

    /// <summary>Refuses if <see cref="Exists"/> is already true for this level — a snapshot is written
    /// only once, on that level's first completion (<c>docs/PROGRESSION.md</c> §2: "loss does not
    /// overwrite").</summary>
    public bool Save(int level, SerializablePlayer player)
    {
        if (Exists(level))
        {
            GhostLog.Warn($"GhostSnapshotFileStore: ghost_a{level}.json already exists under {_dataDir}; refusing to overwrite a historical Ghost.");
            return false;
        }
        Directory.CreateDirectory(_dataDir);
        JsonNode playerNode = JsonSerializer.SerializeToNode(player, JsonSerializationUtility.GetTypeInfo<SerializablePlayer>())
            ?? throw new InvalidOperationException("SerializablePlayer serialized to a null JsonNode.");
        JsonObject root = new()
        {
            ["version"] = SchemaVersion,
            ["ascensionLevel"] = level,
            ["characterId"] = player.CharacterId?.ToString() ?? "null",
            ["savedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["player"] = playerNode,
        };
        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string path = PathFor(level);
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: false);
        GhostLog.Info($"GhostSnapshotFileStore: wrote {path} (character={player.CharacterId}).");
        return true;
    }

    private static List<string> FindMissingIds(SerializablePlayer player)
    {
        List<string> missing = new();
        if (player.CharacterId is not { } characterId || ModelDb.GetByIdOrNull<CharacterModel>(characterId) is null)
        {
            missing.Add($"character:{player.CharacterId}");
        }
        missing.AddRange(player.Deck
            .Where(c => c.Id is not { } id || ModelDb.GetByIdOrNull<CardModel>(id) is null)
            .Select(c => $"card:{c.Id}"));
        missing.AddRange(player.Relics
            .Where(r => r.Id is not { } id || ModelDb.GetByIdOrNull<RelicModel>(id) is null)
            .Select(r => $"relic:{r.Id}"));
        return missing;
    }
}
