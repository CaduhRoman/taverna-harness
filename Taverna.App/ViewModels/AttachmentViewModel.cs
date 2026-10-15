using Avalonia.Media.Imaging;

namespace Taverna.App.ViewModels;

/// <summary>An image stored under .taverna/attachments/, shown as a small thumbnail.</summary>
public sealed class AttachmentViewModel
{
    public string RelativePath { get; }
    public string FileName => Path.GetFileName(RelativePath);
    public Bitmap? Thumbnail { get; }

    public AttachmentViewModel(string projectRoot, string relativePath)
    {
        RelativePath = relativePath;
        try
        {
            using var stream = File.OpenRead(Path.Combine(projectRoot, relativePath));
            Thumbnail = Bitmap.DecodeToHeight(stream, 96);
        }
        catch (Exception) { Thumbnail = null; } // Missing or unreadable: the file name is shown instead.
    }
}
