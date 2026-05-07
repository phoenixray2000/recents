using Recents.App.Models;

namespace Recents.App.Services.Clipboard;

internal static class ClipboardItemSnapshot
{
    public static ClipboardItem ToClipboardItem(this ClipboardFavoriteItem favorite)
    {
        return new ClipboardItem
        {
            Id = favorite.Id,
            Type = favorite.Type,
            CreatedUtc = favorite.SourceCreatedUtc,
            LastUsedUtc = favorite.CreatedUtc,
            Hash = favorite.Hash,
            PreviewText = favorite.PreviewText,
            PlainText = favorite.PlainText,
            TextLength = favorite.TextLength,
            FilePaths = favorite.FilePaths.Select(f => new ClipboardFilePath
            {
                Path = f.Path,
                IsFolder = f.IsFolder,
                ExistsAtCapture = f.ExistsAtCapture
            }).ToList(),
            BlobPath = favorite.BlobPath,
            HtmlBlobPath = favorite.HtmlBlobPath,
            RtfBlobPath = favorite.RtfBlobPath,
            ImagePath = favorite.ImagePath,
            ThumbnailPath = favorite.ThumbnailPath,
            ImageWidth = favorite.ImageWidth,
            ImageHeight = favorite.ImageHeight,
            SizeBytes = favorite.SizeBytes,
            SourceAppName = favorite.SourceAppName,
            SourceAppPath = favorite.SourceAppPath,
            IsFavorite = true
        };
    }
}
