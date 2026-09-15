using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace PsViethoa.FpkgBuilder.Core.Localization;

public sealed record LanguageOption(string Code, string Name);

/// <summary>
/// Bảng chuỗi đa ngôn ngữ (vi/en) nạp từ tài nguyên nhúng Localization/*.json.
/// Đổi ngôn ngữ lúc chạy: các binding tới chỉ mục this[key] được cập nhật qua PropertyChanged("Item[]").
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string Vietnamese = "vi";
    public const string English = "en";
    public const string Korean = "ko";

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(Vietnamese, "Tiếng Việt"),
        new(English, "English"),
        new(Korean, "한국어"),
    ];

    public static Loc Current { get; } = new();

    private static readonly Dictionary<string, string> Fallback = LoadTable(English);

    private Dictionary<string, string> _table;

    private Loc()
    {
        Language = Vietnamese;
        _table = LoadTable(Vietnamese);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string Language { get; private set; }

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (_table.TryGetValue(key, out var value))
        {
            return value;
        }

        return Fallback.TryGetValue(key, out var fallback) ? fallback : key;
    }

    /// <summary>Chuỗi theo khoá.</summary>
    public static string T(string key) => Current.Get(key);

    /// <summary>Chuỗi theo khoá, có định dạng tham số.</summary>
    public static string F(string key, params object?[] args)
    {
        var format = Current.Get(key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    public static string Normalize(string? code)
    {
        if (code == null)
        {
            return Vietnamese;
        }

        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }

        if (code.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            return Korean;
        }

        return Vietnamese;
    }

    public static string DetectSystemLanguage()
    {
        var twoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (string.Equals(twoLetter, "vi", StringComparison.OrdinalIgnoreCase))
        {
            return Vietnamese;
        }

        if (string.Equals(twoLetter, "ko", StringComparison.OrdinalIgnoreCase))
        {
            return Korean;
        }

        return English;
    }

    public void SetLanguage(string? code)
    {
        var normalized = Normalize(code);
        if (normalized == Language)
        {
            return;
        }

        _table = LoadTable(normalized);
        Language = normalized;

        // Binding tới chỉ mục this[key]: WPF lắng nghe "Item[]", Avalonia lắng nghe "Item" hoặc tên rỗng/null
        // (= mọi thuộc tính đổi). Phát đủ các dạng để mọi binding {l:T} cập nhật ngay.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Dictionary<string, string> LoadTable(string code)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"PsViethoa.FpkgBuilder.Core.Localization.{code}.json");
            if (stream == null)
            {
                return table;
            }

            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    table[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (Exception)
        {
            // Thiếu bảng chuỗi thì hiển thị khoá — không được làm ứng dụng sập.
        }

        return table;
    }
}
