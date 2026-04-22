using System.Security.Cryptography;

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
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                // Swallow per-file failures — we still want to process the rest.
                Wipe(file);
            }

            // Remove empty folders deepest-first.
            var dirs = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length)
                .ToList();

            foreach (var d in dirs)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: false); }
                catch { /* best-effort */ }
            }

            try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
            return !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
