namespace Dentiva.Core.Data;

/// <summary>
/// Central resolver for every on-disk location Dentiva uses. All paths live under a single
/// application data root so that backup, restore and uninstall remain predictable.
/// </summary>
public sealed class AppPaths
{
    public const string AttachmentsFolderName = "Attachments";

    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dentiva");

        DatabaseDirectory = Path.Combine(Root, "Database");
        AttachmentsDirectory = Path.Combine(Root, AttachmentsFolderName);
        BackupsDirectory = Path.Combine(Root, "Backups");
        ExportsDirectory = Path.Combine(Root, "Exports");
        LogsDirectory = Path.Combine(Root, "Logs");
        GeneratedDirectory = Path.Combine(Root, "Generated");
        TempDirectory = Path.Combine(Root, "Temp");
        BrandingDirectory = Path.Combine(Root, "Branding");
        DatabaseFile = Path.Combine(DatabaseDirectory, "dentiva.db");
    }

    public string Root { get; }
    public string DatabaseDirectory { get; }
    public string DatabaseFile { get; }
    public string AttachmentsDirectory { get; }
    public string BackupsDirectory { get; }
    public string ExportsDirectory { get; }
    public string LogsDirectory { get; }
    public string GeneratedDirectory { get; }
    public string TempDirectory { get; }
    public string BrandingDirectory { get; }

    public IEnumerable<string> AttachmentCategoryFolders => new[]
    {
        "Patients", "Reports", "XRay", "Documents", "Referrals", "Expenses", "Staff", "Other"
    };

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(ExportsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(GeneratedDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(BrandingDirectory);
        foreach (var folder in AttachmentCategoryFolders)
        {
            Directory.CreateDirectory(Path.Combine(AttachmentsDirectory, folder));
        }
    }

    /// <summary>
    /// Resolves a relative attachment path safely, rejecting traversal outside the attachment root.
    /// </summary>
    public string ResolveAttachment(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Attachment path is required.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Attachment paths must be relative to the attachment store.");
        }

        var root = Path.GetFullPath(AttachmentsDirectory + Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Rejected attachment path outside of the managed attachment store.");
        }

        return combined;
    }

    public long GetAvailableFreeBytes()
    {
        try
        {
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(Root));
            if (string.IsNullOrEmpty(driveRoot))
            {
                return -1;
            }

            return new DriveInfo(driveRoot).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }
}
