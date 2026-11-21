using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PKHeX.Core;

namespace TeraFinder.Core;

public enum BatchBallMode
{
    Default,
    Fixed,
    RandomLegal,
}

/// <summary>One result row, optionally carrying the original RNG result for mismatch detection.</summary>
public readonly record struct BatchRaidEntry(uint Seed, byte GroupID, TeraDetails? Expected = null);

public sealed record BatchExportOptions
{
    public required IReadOnlyList<BatchRaidEntry> Entries { get; init; }
    public required EncounterRaidTF9[] Encounters { get; init; }
    public required GameVersion Version { get; init; }
    public required GameProgress Progress { get; init; }
    public required EventProgress EventProgress { get; init; }
    public required RaidContent Content { get; init; }
    public required TeraRaidMapParent Map { get; init; }
    public required uint ID32 { get; init; }
    public required string OriginalTrainer { get; init; }
    public required int Language { get; init; }
    public required byte TrainerGender { get; init; }
    public required string DestinationDirectory { get; init; }

    public bool ExportPK9 { get; init; } = true;
    /// <summary>BIN contains precisely the same decrypted PK9 bytes as the corresponding PK9 file.</summary>
    public bool ExportBIN { get; init; }
    public bool CreateZIP { get; init; } = true;
    public bool WriteManifest { get; init; } = true;
    public bool StopOnError { get; init; }
    public BatchBallMode BallMode { get; init; } = BatchBallMode.RandomLegal;
    public Ball FixedBall { get; init; } = Ball.Poke;
    /// <summary>Optional allow-list used by RandomLegal. Null means every legally permitted ball.</summary>
    public IReadOnlySet<Ball>? AllowedBalls { get; init; }
}

public readonly record struct BatchExportProgress(int Completed, int Total, int Exported, int Skipped);
public readonly record struct BatchExportResult(string OutputPath, int Total, int Exported, int Skipped, int WrittenFiles);

/// <summary>
/// Serial, cancellable exporter. PKHeX's global legality settings are restored
/// even after a failed file or cancellation. The save file is never modified.
/// </summary>
public static class BatchExportService
{
    private static readonly object LegalityGate = new();

    public static BatchExportResult Export(BatchExportOptions options, IProgress<BatchExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ExportPK9 && !options.ExportBIN)
            throw new ArgumentException("Select at least one output format.", nameof(options));
        if (options.Entries.Count == 0)
            throw new ArgumentException("There are no results to export.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.DestinationDirectory))
            throw new ArgumentException("A destination folder is required.", nameof(options));

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(options.DestinationDirectory);

        var basename = $"TeraFinder_Export_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var output = Path.Combine(options.DestinationDirectory, basename + (options.CreateZIP ? ".zip" : ""));
        var temporary = options.CreateZIP ? output + ".partial" : output + ".partial";
        var exported = 0;
        var skipped = 0;
        var written = 0;
        var manifest = new StringBuilder("Index,Seed,GroupID,Species,Stars,Ball,Status,Files,Reason\r\n");

        try
        {
            if (!options.CreateZIP)
                Directory.CreateDirectory(temporary);

            using var archiveStream = options.CreateZIP
                ? new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                : null;
            using var archive = archiveStream is null ? null : new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false);

            lock (LegalityGate)
            {
                var settings = LegalityUtil.StoreLegalitySettings();
                try
                {
                    LegalityUtil.SetDefaultlegalitySettings();

                    for (var index = 0; index < options.Entries.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = options.Entries[index];
                        try
                        {
                            var file = Generate(entry, options);
                            var prefix = $"#{index + 1:D6}_{(options.Version == GameVersion.VL ? "VL" : "SL")}_{file.PK.Species}_{entry.Seed:X8}_G{entry.GroupID}_{file.Ball}";
                            var filenames = new List<string>(2);
                            if (options.ExportPK9)
                            {
                                var path = $"PK9/{prefix}.pk9";
                                WriteFile(path, file.PK.Data.ToArray(), temporary, archive);
                                filenames.Add(path);
                                written++;
                            }
                            if (options.ExportBIN)
                            {
                                var path = $"BIN/{prefix}.bin";
                                WriteFile(path, file.PK.Data.ToArray(), temporary, archive);
                                filenames.Add(path);
                                written++;
                            }

                            exported++;
                            AddManifestRow(manifest, index + 1, entry, file.PK.Species, file.Stars, file.PK.Ball, "Exported", string.Join(";", filenames), "");
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (!options.StopOnError)
                        {
                            skipped++;
                            AddManifestRow(manifest, index + 1, entry, 0, 0, 0, "Skipped", "", ex.Message);
                        }
                        progress?.Report(new BatchExportProgress(index + 1, options.Entries.Count, exported, skipped));
                    }
                }
                finally
                {
                    LegalityUtil.SetLegalitySettings(settings);
                }
            }

            if (options.WriteManifest)
                WriteFile("manifest.csv", Encoding.UTF8.GetBytes(manifest.ToString()), temporary, archive);

            // The ZIP must be closed before renaming its temporary file.
            // The using declarations above dispose at the end of this scope.
        }
        catch
        {
            Cleanup(temporary);
            throw;
        }

        try
        {
            // ZipArchive and its underlying file have been closed here.
            if (options.CreateZIP)
                File.Move(temporary, output);
            else
                Directory.Move(temporary, output);
            return new BatchExportResult(output, options.Entries.Count, exported, skipped, written);
        }
        catch
        {
            Cleanup(temporary);
            throw;
        }
    }

    private static (PK9 PK, Ball Ball, int Stars) Generate(BatchRaidEntry entry, BatchExportOptions options)
    {
        if (!EncounterRaidTF9.TryGenerateTeraDetails(entry.Seed, options.Encounters,
                options.Version, options.Progress, options.EventProgress, options.Content,
                options.Map, options.ID32, entry.GroupID, out var encounter, out var rng)
            || encounter is null || rng is null)
            throw new InvalidOperationException("The raid seed cannot be matched to an encounter.");

        if (!encounter.IsCatchable)
            throw new InvalidOperationException("This event Pokémon cannot be caught.");

        if (entry.Expected is { } expected &&
            (expected.EC != rng.Value.EC || expected.PID != rng.Value.PID ||
             expected.Species != rng.Value.Species || expected.Stars != rng.Value.Stars ||
             expected.EventIndex != rng.Value.EventIndex))
            throw new InvalidOperationException("The displayed result differs from the regenerated raid. Re-run the search.");

        // GeneratePK9 returns its generated file and legality details even if the
        // original default ball was not legal. Validate the final ball below.
        encounter.GeneratePK9(rng.Value, options.ID32, options.Version, options.OriginalTrainer,
            options.Language, options.TrainerGender, out var pk, out _);

        var ball = ResolveBall(pk, encounter, options);
        pk.Ball = (byte)ball;
        pk.RefreshChecksum();

        var legality = new LegalityAnalysis(pk);
        // Preserve the existing single-PK9 export's known EC==PID exception.
        var knownECException = rng.Value.EC == rng.Value.PID &&
            legality.Results.Any(z => z.Identifier == CheckIdentifier.EC && z.Judgement == Severity.Invalid);
        if (!legality.Valid && !knownECException)
            throw new InvalidOperationException("PKHeX legality validation failed: " + legality.Report());

        return (pk, ball, rng.Value.Stars);
    }

    public static Ball ResolveBall(PK9 pk, EncounterRaidTF9 encounter, BatchExportOptions options)
    {
        if (options.BallMode == BatchBallMode.Default)
            return (Ball)pk.Ball;

        Span<Ball> legal = stackalloc Ball[BallApplicator.MaxBallSpanAlloc];
        var count = BallApplicator.GetLegalBalls(legal, pk, encounter);
        var available = legal[..count];

        if (options.BallMode == BatchBallMode.Fixed)
        {
            if (!available.Contains(options.FixedBall))
                throw new InvalidOperationException($"Ball {options.FixedBall} is not legal for this encounter.");
            return options.FixedBall;
        }

        if (options.BallMode == BatchBallMode.RandomLegal)
        {
            Span<Ball> candidates = stackalloc Ball[BallApplicator.MaxBallSpanAlloc];
            var validCount = 0;
            foreach (var ball in available)
            {
                if (options.AllowedBalls is null || options.AllowedBalls.Contains(ball))
                    candidates[validCount++] = ball;
            }
            if (validCount == 0)
                throw new InvalidOperationException("No legal ball remains in the selected ball pool.");
            return candidates[RandomNumberGenerator.GetInt32(validCount)];
        }

        throw new ArgumentOutOfRangeException(nameof(options), "Unknown ball policy.");
    }

    private static void WriteFile(string name, byte[] contents, string temp, ZipArchive? archive)
    {
        if (archive is not null)
        {
            var file = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = file.Open();
            stream.Write(contents);
        }
        else
        {
            var path = Path.Combine(temp, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
        }
    }

    private static void AddManifestRow(StringBuilder sb, int index, BatchRaidEntry entry,
        ushort species, int stars, byte ball, string status, string files, string reason)
    {
        sb.Append(index).Append(',').Append(entry.Seed.ToString("X8")).Append(',')
          .Append(entry.GroupID).Append(',').Append(species).Append(',')
          .Append(stars).Append(',').Append(ball).Append(',')
          .Append(Csv(status)).Append(',').Append(Csv(files)).Append(',').Append(Csv(reason))
          .Append("\r\n");
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";

    private static void Cleanup(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
