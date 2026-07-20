using System.Text.Json;

namespace JohnnyAppleseed.Save;

/// <summary>
/// Reads and writes the single auto-save slot as JSON in the app-data folder.
///
/// Design goals:
///   • Atomic writes — never leave a half-written save if the process dies mid-save.
///   • Corruption-tolerant — a damaged file is set aside (.bak) rather than crashing.
///   • Version-tolerant — older documents are migrated up to the current schema.
///
/// The save path is overridable (<see cref="SavePath"/>) so tests can run against
/// a temp file without touching the user's real save.
/// </summary>
static class SaveSystem
{
    /// <summary>
    /// Current on-disk schema version. Bump this whenever a change can't be
    /// expressed purely by adding/removing optional fields, and add a matching
    /// step in <see cref="Migrate"/>.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    private const string FileName = "savegame.json";

    private static string _savePath = System.IO.Path.Combine(AppData.Path, FileName);

    /// <summary>Absolute path of the save file. Settable for tests.</summary>
    public static string SavePath
    {
        get => _savePath;
        set => _savePath = value;
    }

    public static bool Exists() => File.Exists(_savePath);

    /// <summary>
    /// Load the save, migrating it to the current schema. Returns <c>null</c> when
    /// no save exists or the file is unreadable/corrupt (the corrupt file is moved
    /// aside to "<c>&lt;name&gt;.bak</c>" first).
    /// </summary>
    public static SaveData? Load()
    {
        if (!File.Exists(_savePath))
            return null;

        try
        {
            string json = File.ReadAllText(_savePath);
            var data = JsonSerializer.Deserialize(json, SaveJsonContext.Default.SaveData);
            if (data == null)
                return null;

            Migrate(data);
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[save] failed to load '{_savePath}': {ex.Message}");
            TryQuarantineCorruptFile();
            return null;
        }
    }

    /// <summary>
    /// Serialize and write atomically: write a sibling ".tmp" then replace the
    /// real file with it, so readers never observe a partial document.
    /// </summary>
    public static void Save(SaveData data)
    {
        data.FormatVersion = CurrentFormatVersion;
        data.GameVersion = BuildInfo.Version;
        data.UpdatedAtUtc = DateTime.UtcNow;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_savePath)!);

        string json = JsonSerializer.Serialize(data, SaveJsonContext.Default.SaveData);
        string tmp = _savePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _savePath, overwrite: true);
    }

    public static void Delete()
    {
        if (File.Exists(_savePath))
            File.Delete(_savePath);
    }

    // ── migration ───────────────────────────────────────────────────────────

    /// <summary>
    /// Upgrade an older document in place to <see cref="CurrentFormatVersion"/>.
    /// Each case falls through to the next so a very old save is stepped up one
    /// version at a time. There are no historical versions yet (v1 is the first),
    /// so this is currently a no-op scaffold.
    /// </summary>
    private static void Migrate(SaveData data)
    {
        // Newer-than-known save (written by a future build): leave its extra data
        // intact and just treat it as current — do not downgrade the number, or a
        // later save from this build would silently strip fields we don't model.
        if (data.FormatVersion >= CurrentFormatVersion)
            return;

        switch (data.FormatVersion)
        {
            // case 0: ...transform v0 → v1...; data.FormatVersion = 1; goto case 1;
            default:
                break;
        }

        data.FormatVersion = CurrentFormatVersion;
    }

    private static void TryQuarantineCorruptFile()
    {
        try
        {
            string bak = _savePath + ".bak";
            File.Copy(_savePath, bak, overwrite: true);
            Console.Error.WriteLine($"[save] corrupt save moved aside to '{bak}'");
        }
        catch
        {
            // Best-effort only.
        }
    }
}
