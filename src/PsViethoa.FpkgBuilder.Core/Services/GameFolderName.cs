using System.Text;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Tên thư mục xuất "&lt;Tên game&gt; &lt;PPSAxxxxx&gt;" dùng chung cho Giải nén gói và FPKG trên PS5: bỏ ký tự Windows/macOS
/// không cho dùng trong tên thư mục, ™ ® ©, dấu ':' đổi thành khoảng trắng.
/// </summary>
public static class GameFolderName
{
    private const int MaxLength = 90;
    private static readonly HashSet<char> Invalid = new(Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']));

    /// <summary>"Hades" + "PPSA03355" → "Hades PPSA03355"; tên trống thì chỉ còn ID; tên đã chứa ID thì không lặp lại.</summary>
    public static string For(string? title, string? titleId)
    {
        var id = (titleId ?? string.Empty).Trim().ToUpperInvariant();
        var name = Safe(title);
        if (name.Length == 0)
        {
            return id;
        }

        return id.Length == 0 || name.Contains(id, StringComparison.OrdinalIgnoreCase) ? name : name + " " + id;
    }

    /// <summary>"EP0082-PPSA08668_00-..." → "PPSA08668" (ký tự 7..16 của Content ID); trống nếu không đúng dạng.</summary>
    public static string TitleIdOf(string? contentId)
    {
        if (string.IsNullOrEmpty(contentId) || contentId.Length < 16 || contentId[6] != '-')
        {
            return string.Empty;
        }

        var id = contentId.Substring(7, 9);
        return id.All(char.IsAsciiLetterOrDigit) ? id.ToUpperInvariant() : string.Empty;
    }

    /// <summary>Làm sạch một chuỗi thành tên thư mục hợp lệ (gộp khoảng trắng, cắt dấu chấm cuối, tối đa 90 ký tự).</summary>
    public static string Safe(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (c is '™' or '®' or '©')
            {
                continue;
            }

            if (c == ':' || Invalid.Contains(c) || char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (!space && builder.Length > 0)
                {
                    builder.Append(' ');
                    space = true;
                }

                continue;
            }

            builder.Append(c);
            space = false;
        }

        var name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length > MaxLength)
        {
            name = name[..MaxLength].TrimEnd('.', ' ');
        }

        return name;
    }
}
