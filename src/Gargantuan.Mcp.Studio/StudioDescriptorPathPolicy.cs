namespace Gargantuan.Mcp.Studio;

internal static class StudioDescriptorPathPolicy
{
    internal static string ValidateForRead(string DescriptorPath)
    {
        if (string.IsNullOrWhiteSpace(DescriptorPath) || !Path.IsPathFullyQualified(DescriptorPath))
            throw new ArgumentException("An absolute Studio bridge descriptor path is required.", nameof(DescriptorPath));
        string FullPath = Path.GetFullPath(DescriptorPath);
        string LocalRoot = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string Relative = Path.GetRelativePath(LocalRoot, FullPath);
        if (Relative == "." || Path.IsPathFullyQualified(Relative) ||
            Relative == ".." || Relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The Studio bridge descriptor must be inside LocalApplicationData.", nameof(DescriptorPath));

        RejectReparsePoint(LocalRoot);
        string Current = LocalRoot;
        foreach (string Component in Relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            Current = Path.Combine(Current, Component);
            if (File.Exists(Current) || Directory.Exists(Current)) RejectReparsePoint(Current);
        }
        return FullPath;
    }

    private static void RejectReparsePoint(string PathValue)
    {
        if ((File.GetAttributes(PathValue) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("The Studio bridge descriptor path cannot contain symbolic links or reparse points.");
    }
}
