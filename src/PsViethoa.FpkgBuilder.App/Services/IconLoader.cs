using Avalonia.Media.Imaging;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.App.Services;

/// <summary>Đọc icon0.png của nguồn (tệp trên đĩa hoặc bytes lấy từ ảnh đĩa) thành Bitmap thu nhỏ; lỗi thì trả null.</summary>
public static class IconLoader
{
    public static Bitmap? Load(SourceMetadata metadata, int width = 256)
    {
        try
        {
            if (metadata.IconBytes is { Length: > 0 } bytes)
            {
                using var memory = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(memory, width);
            }

            if (metadata.IconPath != null && File.Exists(metadata.IconPath))
            {
                using var stream = File.OpenRead(metadata.IconPath);
                return Bitmap.DecodeToWidth(stream, width);
            }
        }
        catch (Exception)
        {
        }

        return null;
    }
}
