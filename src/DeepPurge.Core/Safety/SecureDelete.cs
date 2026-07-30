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
    /// <summary>Securely wipes a single file. Returns false on failure.</summary>
    public static bool Wipe(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (HandleBoundFileOperations.SecureDeleteFile(path, out var reason))
            return true;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Log.Warn($"SecureDelete.Wipe failed for '{path}': {reason}");
        }
        return false;
    }

    /// <summary>
    /// Wipes every file in a directory tree, then removes the directories.
    /// Walks leaves-first to avoid trying to delete non-empty parents.
    /// </summary>
    public static bool WipeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (HandleBoundFileOperations.SecureDeleteDirectoryTree(path, out var reason))
            return true;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Log.Warn($"SecureDelete.WipeDirectory failed for '{path}': {reason}");
        }
        return false;
    }

}
