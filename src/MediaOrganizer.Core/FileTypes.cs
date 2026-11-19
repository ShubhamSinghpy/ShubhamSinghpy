namespace MediaOrganizer.Core;

public enum ExtKind { Other, Photo, Raw, Video, MediaLike }

public static class FileTypes
{
    private static readonly HashSet<string> PhotoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".heic", ".heif", ".tif", ".tiff",
        ".bmp", ".webp", ".gif", ".avif"
    };

    private static readonly HashSet<string> RawExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".sr2", ".srf", ".orf", ".rw2", ".raf",
        ".dng", ".pef", ".srw", ".raw", ".3fr", ".erf", ".kdc", ".mrw", ".x3f", ".rwl"
    };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".3g2", ".wmv", ".mpg", ".mpeg",
        ".mts", ".m2ts", ".webm", ".flv", ".mxf"
    };

    /// <summary>Media-looking formats we cannot reliably read; they go to Unclassified.</summary>
    private static readonly HashSet<string> MediaLikeExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".psd", ".svg", ".ico", ".ogv", ".vob", ".divx", ".rm", ".rmvb", ".xcf", ".ai", ".eps", ".exr", ".hdr"
    };

    public static ExtKind Kind(string extension)
    {
        if (PhotoExt.Contains(extension)) return ExtKind.Photo;
        if (RawExt.Contains(extension)) return ExtKind.Raw;
        if (VideoExt.Contains(extension)) return ExtKind.Video;
        if (MediaLikeExt.Contains(extension)) return ExtKind.MediaLike;
        return ExtKind.Other;
    }
}
