using System.Management;
using System.Security.Cryptography;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Safety;

/// <summary>
/// Privacy-grade deletion. Per current research (see BleachBit/PrivaZer docs),
/// multi-pass DoD 5220.22-M overwrites are obsolete on SSDs — they waste write
/// cycles without improving destruction. We do a single pass of cryptographic
/// random data, rename to a random filename to destroy the MFT entry, then
/// delete. On directories, every file is wiped before the tree is removed.
///
/// This is exposed as an option for the leftover/evidence flows — not the
/// default — because secure delete is slower and irreversible.
/// </summary>
public static class SecureDelete
{
    private const int BufferSize = 64 * 1024;

    /// <summary>Securely wipes a single file. Returns false on failure.</summary>
    public static bool Wipe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            // Step 1: remove read-only / hidden so we can open write-shared.
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);

            // Step 2: single-pass cryptographic random overwrite.
            long size = new FileInfo(path).Length;
            if (size > 0)
            {
                using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.WriteThrough);

                var buffer = new byte[BufferSize];
                long remaining = size;
                while (remaining > 0)
                {
                    RandomNumberGenerator.Fill(buffer);
                    int toWrite = (int)Math.Min(remaining, buffer.Length);
                    fs.Write(buffer, 0, toWrite);
                    remaining -= toWrite;
                }
                fs.Flush(flushToDisk: true);
            }

            // Step 3: rename to an opaque name so the MFT entry doesn't leak the
            // original filename after deletion.
            var randomName = Path.Combine(
                Path.GetDirectoryName(path) ?? "",
                Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) + ".tmp");
            File.Move(path, randomName);

            // Step 4: delete.
            File.Delete(randomName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wipes every file in a directory tree, then removes the directories.
    /// Walks leaves-first to avoid trying to delete non-empty parents.
    /// </summary>
    public static bool WipeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

        try
        {
            foreach (var file in SafetyGuard.SafeEnumerateFiles(path))
                Wipe(file);

            foreach (var d in SafetyGuard.SafeEnumerateDirectories(path))
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: false); }
                catch (Exception ex) { Log.Warn($"Failed to remove subdirectory '{d}': {ex.Message}"); }
            }

            try { Directory.Delete(path, recursive: false); }
            catch (Exception ex) { Log.Warn($"Failed to remove root directory '{path}': {ex.Message}"); }
            return !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<long> WipeFreeSpaceAsync(
        string drivePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var driveRoot = Path.GetPathRoot(drivePath) ?? @"C:\";
        var isSsd = DetectSsd(driveRoot);
        progress?.Report(isSsd ? $"SSD detected on {driveRoot} — single-pass fill" : $"HDD detected on {driveRoot} — single-pass fill");

        var tempDir = Path.Combine(driveRoot, ".deeppurge_wipe_" + Guid.NewGuid().ToString("N")[..8]);
        long totalWritten = 0;
        try
        {
            Directory.CreateDirectory(tempDir);
            var fillBuffer = new byte[1024 * 1024];
            int fileIndex = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var filePath = Path.Combine(tempDir, $"wipe_{fileIndex++}.tmp");
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, fillBuffer.Length, FileOptions.WriteThrough);
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        RandomNumberGenerator.Fill(fillBuffer);
                        fs.Write(fillBuffer, 0, fillBuffer.Length);
                        totalWritten += fillBuffer.Length;
                        if (totalWritten % (100L * 1024 * 1024) == 0)
                            progress?.Report($"Filling free space: {totalWritten / (1024 * 1024)} MB written...");
                    }
                }
                catch (IOException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"WipeFreeSpace: {ex.Message}"); }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex) { Log.Warn($"Failed to clean up wipe temp directory '{tempDir}': {ex.Message}"); }
        }

        progress?.Report($"Free space wipe complete: {totalWritten / (1024 * 1024)} MB overwritten");
        return totalWritten;
    }

    private static bool DetectSsd(string driveRoot)
    {
        try
        {
            var driveLetter = driveRoot.TrimEnd('\\').TrimEnd(':');
            using var searcher = new ManagementObjectSearcher(
                $"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceID IN " +
                $"(SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter='{driveLetter}')");
            searcher.Scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
            foreach (ManagementBaseObject baseObj in searcher.Get())
            {
                using var obj = baseObj;
                var mediaType = Convert.ToInt32(obj["MediaType"]);
                return mediaType == 4; // 4 = SSD, 3 = HDD
            }
        }
        catch (Exception ex) { Log.Warn($"SSD detection failed for '{driveRoot}': {ex.Message}"); }
        return false;
    }
}
