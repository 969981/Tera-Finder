using System.IO.Compression;
using PKHeX.Core;
using TeraFinder.Core;
using Xunit;

namespace TeraFinder.Tests;

public class BatchExportTests
{
    private static BatchExportOptions GetOptions(string folder, bool zip)
    {
        var (distribution, _) = ResourcesUtil.GetAllEventEncounters();
        var eventEncounters = distribution.Where(x => x.Identifier / 100u == 20230622u).ToArray();
        Assert.NotEmpty(eventEncounters);
        return new BatchExportOptions
        {
            Entries = [new BatchRaidEntry(0xDB72E373u, 1)],
            Encounters = eventEncounters,
            Version = GameVersion.VL,
            Progress = GameProgress.Unlocked6Stars,
            EventProgress = EventProgress.Stage3,
            Content = RaidContent.Event,
            Map = TeraRaidMapParent.Paldea,
            ID32 = 0,
            OriginalTrainer = "TF",
            Language = (int)LanguageID.English,
            TrainerGender = 0,
            DestinationDirectory = folder,
            ExportPK9 = true,
            ExportBIN = true,
            CreateZIP = zip,
            WriteManifest = true,
            BallMode = BatchBallMode.RandomLegal,
        };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExportsIdenticalPK9AndBinBytesAndManifest(bool zipped)
    {
        LegalityUtil.SetDefaultlegalitySettings();
        var temp = Path.Combine(Path.GetTempPath(), "TeraFinder_batchtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = BatchExportService.Export(GetOptions(temp, zipped));
            Assert.Equal(1, result.Exported);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(2, result.WrittenFiles);
            Assert.True(zipped ? File.Exists(result.OutputPath) : Directory.Exists(result.OutputPath));

            byte[] pk9;
            byte[] bin;
            string manifest;
            if (zipped)
            {
                using var archive = ZipFile.OpenRead(result.OutputPath);
                Assert.Equal(3, archive.Entries.Count);
                var pkEntry = Assert.Single(archive.Entries, z => z.FullName.EndsWith(".pk9"));
                var binEntry = Assert.Single(archive.Entries, z => z.FullName.EndsWith(".bin"));
                using var pkBuffer = new MemoryStream();
                using var binBuffer = new MemoryStream();
                using (var stream = pkEntry.Open()) stream.CopyTo(pkBuffer);
                using (var stream = binEntry.Open()) stream.CopyTo(binBuffer);
                pk9 = pkBuffer.ToArray();
                bin = binBuffer.ToArray();
                using var reader = new StreamReader(Assert.Single(archive.Entries, z => z.FullName == "manifest.csv").Open());
                manifest = reader.ReadToEnd();
            }
            else
            {
                pk9 = File.ReadAllBytes(Assert.Single(Directory.GetFiles(result.OutputPath, "*.pk9", SearchOption.AllDirectories)));
                bin = File.ReadAllBytes(Assert.Single(Directory.GetFiles(result.OutputPath, "*.bin", SearchOption.AllDirectories)));
                manifest = File.ReadAllText(Path.Combine(result.OutputPath, "manifest.csv"));
            }

            Assert.Equal(pk9, bin);
            Assert.Contains("DB72E373", manifest);
            Assert.Contains($",{(ushort)Species.Gimmighoul},3,", manifest); // national species ID and real stars
            Assert.Contains("Exported", manifest);
            Assert.True(new PK9(pk9).ChecksumValid);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void IllegalFixedBallIsSkippedAndRecorded()
    {
        LegalityUtil.SetDefaultlegalitySettings();
        var temp = Path.Combine(Path.GetTempPath(), "TeraFinder_batchtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = GetOptions(temp, zip: true) with { BallMode = BatchBallMode.Fixed, FixedBall = Ball.Cherish };
            var result = BatchExportService.Export(options);
            Assert.Equal(0, result.Exported);
            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, result.WrittenFiles);
            using var zip = ZipFile.OpenRead(result.OutputPath);
            Assert.Single(zip.Entries);
            using var reader = new StreamReader(zip.Entries[0].Open());
            Assert.Contains("Skipped", reader.ReadToEnd());
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void CancellationLeavesNoPartialFiles()
    {
        var temp = Path.Combine(Path.GetTempPath(), "TeraFinder_batchtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                BatchExportService.Export(GetOptions(temp, zip: true), cancellationToken: cts.Token));
            Assert.False(Directory.Exists(temp));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }
}
