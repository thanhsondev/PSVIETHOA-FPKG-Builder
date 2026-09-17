using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// <c>json.dumps(value, indent=2, ensure_ascii=False)</c> của CPython — để param.json chuẩn hoá mà công cụ ghi vào
/// <c>.gp5-assets</c> giống từng byte với <c>write_standard_param</c> của create-gp5-from-folder.py: thụt 2 khoảng trắng,
/// <c>": "</c> sau khoá, mảng/đối tượng rỗng là <c>[]</c>/<c>{}</c>, ký tự ngoài ASCII giữ nguyên, chỉ thoát <c>"</c>, <c>\</c>
/// và ký tự điều khiển (\b \f \n \r \t, còn lại \u00xx chữ thường), số nguyên giữ nguyên, số thực theo repr() của Python.
/// </summary>
public static class PythonJson
{
    public static string Serialize(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node, 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? node, int level)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    builder.Append("{}");
                    break;
                }

                builder.Append("{\n");
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first)
                    {
                        builder.Append(",\n");
                    }

                    first = false;
                    Indent(builder, level + 1);
                    WriteString(builder, key);
                    builder.Append(": ");
                    Write(builder, value, level + 1);
                }

                builder.Append('\n');
                Indent(builder, level);
                builder.Append('}');
                break;
            case JsonArray array:
                if (array.Count == 0)
                {
                    builder.Append("[]");
                    break;
                }

                builder.Append("[\n");
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(",\n");
                    }

                    Indent(builder, level + 1);
                    Write(builder, array[index], level + 1);
                }

                builder.Append('\n');
                Indent(builder, level);
                builder.Append(']');
                break;
            case JsonValue value:
                WriteValue(builder, value);
                break;
            default:
                throw new NotSupportedException(node.GetType().Name);
        }
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    WriteString(builder, element.GetString()!);
                    return;
                case JsonValueKind.Number:
                    WriteNumber(builder, element.GetRawText());
                    return;
                case JsonValueKind.True:
                    builder.Append("true");
                    return;
                case JsonValueKind.False:
                    builder.Append("false");
                    return;
                case JsonValueKind.Null:
                    builder.Append("null");
                    return;
            }
        }

        // Giá trị do mã tạo (node["x"] = 0 / "standard" / true …): JsonValue bọc kiểu .NET.
        if (value.TryGetValue<string>(out var text))
        {
            WriteString(builder, text);
        }
        else if (value.TryGetValue<bool>(out var flag))
        {
            builder.Append(flag ? "true" : "false");
        }
        else if (value.TryGetValue<long>(out var integer))
        {
            builder.Append(integer.ToString(CultureInfo.InvariantCulture));
        }
        else if (value.TryGetValue<double>(out var real))
        {
            builder.Append(PythonFloat(real));
        }
        else
        {
            WriteNumber(builder, value.ToJsonString());
        }
    }

    /// <summary>Số nguyên giữ nguyên chữ số; số thực (có '.', 'e', 'E') in theo repr() của Python.</summary>
    private static void WriteNumber(StringBuilder builder, string raw)
    {
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
        {
            builder.Append(raw);
            return;
        }

        builder.Append(double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? PythonFloat(real) : raw);
    }

    private static string PythonFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        // repr(): chuỗi ngắn nhất đọc lại đúng giá trị; dạng thập phân khi 1e-4 <= |x| < 1e16, còn lại dạng mũ "1e+16".
        var magnitude = Math.Abs(value);
        if (value == 0 || (magnitude >= 1e-4 && magnitude < 1e16))
        {
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            if (text.Contains('E'))
            {
                text = decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
                    ? exact.ToString(CultureInfo.InvariantCulture)
                    : text;
            }

            return text.Contains('.') ? text : text + ".0";
        }

        var scientific = value.ToString("R", CultureInfo.InvariantCulture);
        var e = scientific.IndexOf('E');
        if (e < 0)
        {
            scientific = value.ToString("0.################E+0", CultureInfo.InvariantCulture);
            e = scientific.IndexOf('E');
        }

        var mantissa = scientific[..e];
        var exponent = int.Parse(scientific[(e + 1)..], CultureInfo.InvariantCulture);
        return mantissa + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private static void Indent(StringBuilder builder, int level) => builder.Append(' ', level * 2);
}
