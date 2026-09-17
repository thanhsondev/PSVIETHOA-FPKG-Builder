using System.Globalization;
using System.Text;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// <c>str.casefold()</c> của CPython và phép so sánh chuỗi theo mã điểm Unicode như Python — để sắp xếp tệp trong dự án GP5
/// đúng thứ tự của <c>create-gp5-from-folder.py</c> (<c>sorted(entries, key=lambda e: e.name.casefold())</c>). Thứ tự này quyết định
/// bố cục ảnh PFS mà Publishing Tools tạo ra, nên phải giống từng ký tự chứ không dùng ToLowerInvariant (khác ở ß → ss, các chữ ghép
/// ﬁ/ﬂ, ς, İ, …). Bảng lấy từ Services/PythonCaseFold.txt (sinh bằng scripts/gen-casefold.py).
/// </summary>
public static class PythonCaseFold
{
    private static readonly Lazy<Dictionary<int, string>> Table = new(Load);

    private static Dictionary<int, string> Load()
    {
        var table = new Dictionary<int, string>(1600);
        using var stream = typeof(PythonCaseFold).Assembly.GetManifestResourceStream("PsViethoa.FpkgBuilder.Core.Services.PythonCaseFold.txt")
                           ?? throw new InvalidOperationException("PythonCaseFold.txt resource is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var codePoint = int.Parse(line.AsSpan(0, colon), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var folded = new StringBuilder();
            foreach (var part in line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                folded.Append(char.ConvertFromUtf32(int.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }

            table[codePoint] = folded.ToString();
        }

        return table;
    }

    /// <summary>Tương đương <c>value.casefold()</c> của Python (gập chữ hoa/thường đầy đủ theo CaseFolding.txt).</summary>
    public static string Fold(string value)
    {
        var table = Table.Value;
        StringBuilder? builder = null;
        var index = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (table.TryGetValue(rune.Value, out var folded))
            {
                builder ??= new StringBuilder(value.Length + 8).Append(value, 0, index);
                builder.Append(folded);
            }
            else
            {
                builder?.Append(rune.ToString());
            }

            index += rune.Utf16SequenceLength;
        }

        return builder?.ToString() ?? value;
    }

    /// <summary>So sánh hai chuỗi theo từng mã điểm Unicode (cách Python so sánh <c>str</c>), không phải theo đơn vị UTF-16.</summary>
    public static int CompareCodePoints(string left, string right)
    {
        var a = left.EnumerateRunes().GetEnumerator();
        var b = right.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasA = a.MoveNext();
            var hasB = b.MoveNext();
            if (!hasA || !hasB)
            {
                return hasA.CompareTo(hasB);
            }

            var difference = a.Current.Value.CompareTo(b.Current.Value);
            if (difference != 0)
            {
                return difference;
            }
        }
    }

    /// <summary>Bộ so sánh dùng cho <c>sorted(names, key=str.casefold)</c>: gập rồi so theo mã điểm; sắp xếp ổn định để hoà thì giữ thứ tự cũ.</summary>
    public static readonly IComparer<string> NameComparer = Comparer<string>.Create((left, right) => CompareCodePoints(Fold(left), Fold(right)));
}
