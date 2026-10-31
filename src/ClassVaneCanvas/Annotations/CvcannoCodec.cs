using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ClassVaneCanvas.Models.Annotations;
using ClassVaneCanvas.Models.Ink;

namespace ClassVaneCanvas.Annotations;

/// <summary>
/// <c>.cvcanno</c> 批注包的默认读写实现（<see cref="System.Text.Json"/>，UTF-8 无 BOM）。
///
/// 设计要点：
/// · 只读写**独立配套文件**，绝不修改原始 PPT / pptx 压缩包、不写 CustomXml；
/// · 采样点用紧凑数组 <c>[x, y, pressure, tiltX, tiltY, timeMs]</c> 并对浮点做轻量舍入，
///   一条 5000 点硬笔笔迹约占 0.2 MB，远低于 400 KB 上限，U 盘拷贝 / 同步都很轻松；
/// · 文件里**不包含任何播放进度**（进度只存本机 SQLite）；
/// · 通过 <c>coursewareHash</c>（PPT 文件 SHA256）与课件绑定，读取时严格校验格式、版本与哈希。
/// </summary>
public sealed class CvcannoCodec : IAnnotationPackageCodec
{
    /// <summary>坐标（X / Y）保留的小数位数（页面逻辑像素，2 位远超屏幕可见精度）。</summary>
    internal const int CoordinateDecimals = 2;

    /// <summary>压感与笔尖倾斜保留的小数位数（原始归一化值，3 位对笔迹外观无可见影响）。</summary>
    internal const int SensorDecimals = 3;

    /// <summary>写入过程中的临时文件后缀（写坏也只坏临时文件，原批注安然无恙）。</summary>
    private const string TempFileSuffix = ".tmp";

    /// <summary>
    /// 本格式使用的 JSON 配置（全进程共享，请勿修改）：
    /// camelCase 属性名、不缩进、null 不写出。
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <inheritdoc />
    public AnnotationDocument Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        AnnotationDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<AnnotationDocument>(stream, SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or NotSupportedException)
        {
            throw new InvalidDataException(
                "批注文件内容无法解析，可能已损坏或被改动过。请重新导出该课件批注，或删除这个 .cvcanno 文件后重新批注。",
                ex);
        }

        if (document is null)
        {
            throw new InvalidDataException("批注文件内容为空，没有可读取的批注数据。");
        }

        ValidateHeader(document);
        NormalizeCollections(document);
        return document;
    }

    /// <summary>
    /// 把批注文档写入流（UTF-8 无 BOM）。
    /// 写入前会把文件头（<see cref="AnnotationDocument.Format"/> 与
    /// <see cref="AnnotationDocument.FormatVersion"/>）规范化为当前格式常量，
    /// 确保本软件产出的批注包一定能被自己读回；其余字段原样写出。
    /// </summary>
    public void Write(Stream stream, AnnotationDocument document)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(document);

        // 规范化文件头：保证本软件写出的批注包一定能被自己读回
        document.Format = CvcannoFormat.Identifier;
        document.FormatVersion = CvcannoFormat.Version;

        JsonSerializer.Serialize(stream, document, SerializerOptions);
    }

    /// <inheritdoc />
    public AnnotationDocument ReadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("批注文件路径不能为空。", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"找不到批注文件：{fullPath}", fullPath);
        }

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream);
    }

    /// <inheritdoc />
    public void WriteFile(string path, AnnotationDocument document)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("批注文件路径不能为空。", nameof(path));
        }

        ArgumentNullException.ThrowIfNull(document);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = fullPath + TempFileSuffix;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Write(stream, document);
                stream.Flush(flushToDisk: true);
            }

            // 原子替换：先写临时文件再改名，写一半失败也不会破坏已有批注
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>写入前的格式校验（老师能看懂的中文提示）。</summary>
    private static void ValidateHeader(AnnotationDocument document)
    {
        if (!string.Equals(document.Format, CvcannoFormat.Identifier, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"这不是 ClassVane Canvas 批注文件（格式标识为「{Describe(document.Format)}」，" +
                $"应为「{CvcannoFormat.Identifier}」）。请确认选择的是课件对应的 .cvcanno 文件。");
        }

        if (document.FormatVersion > CvcannoFormat.Version)
        {
            throw new InvalidDataException(
                $"批注文件版本（{document.FormatVersion}）高于当前软件支持的版本（{CvcannoFormat.Version}），" +
                "请升级 ClassVane Canvas 后再打开这份批注。");
        }

        if (string.IsNullOrWhiteSpace(document.CoursewareHash))
        {
            throw new InvalidDataException("批注文件缺少课件哈希（coursewareHash），无法确认它属于哪份课件，已停止加载。");
        }

        var hash = document.CoursewareHash.Trim();
        if (!AnnotationHashRules.IsSha256Hex(hash))
        {
            throw new InvalidDataException(
                $"批注文件中的课件哈希格式不正确（应为 64 位十六进制，实际为「{Describe(hash)}」），文件可能已损坏。");
        }

        // 统一按小写十六进制保存，避免大小写不同导致同一份课件被当成两份
        document.CoursewareHash = hash.ToLowerInvariant();
    }

    /// <summary>兼容被手改成 <c>null</c> 的集合字段，避免后续渲染 / 合并时空引用。</summary>
    private static void NormalizeCollections(AnnotationDocument document)
    {
        if (document.Pages is null)
        {
            document.Pages = new List<InkPage>();
        }

        foreach (var page in document.Pages)
        {
            if (page.Strokes is null)
            {
                page.Strokes = new List<InkStroke>();
            }

            foreach (var stroke in page.Strokes)
            {
                if (stroke.Points is null)
                {
                    stroke.Points = new List<InkPoint>();
                }

                stroke.Id ??= string.Empty;
                stroke.OriginDeviceId ??= string.Empty;
            }
        }
    }

    /// <summary>清理临时文件（清理失败不掩盖真正的异常）。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时文件清理失败无关紧要，下次写入会覆盖
        }
        catch (UnauthorizedAccessException)
        {
            // 同上：只读目录等情况下保持原异常向上抛
        }
    }

    /// <summary>把可能为空 / 超长的文本裁剪成适合放进提示语的短串。</summary>
    private static string Describe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "空";
        }

        return value.Length <= 48 ? value : value[..48] + "…";
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // 中文课件名直写不转义（默认编码器会把「语文课件.pptx」写成 \u8BED\u6587…，体积更大且无法直接查看）
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            // 只读计算属性（totalStrokeCount / isEmpty / hasInk…）不写进文件
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropReadOnlyProperties } },
        };

        // 采样点紧凑编码 + 笔触类型用枚举名（HardPen / Marker / Highlighter / Eraser），便于人工排查
        options.Converters.Add(new InkPointJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>去掉只有 getter 的计算属性，它们属于运行时状态，不属于文件内容。</summary>
    private static void DropReadOnlyProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (typeInfo.Properties[i].Set is null)
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }
}

/// <summary>
/// <see cref="InkPoint"/> 的紧凑编码：数组 <c>[x, y, pressure, tiltX, tiltY, timeMs]</c>。
/// 相比对象形式省掉 5 个属性名（约 60 字节 / 点），5000 点笔迹可省下约 0.3 MB。
/// 读取时兼容数组形式，也兼容对象形式（便于手工修复或将来扩展）。
/// </summary>
internal sealed class InkPointJsonConverter : JsonConverter<InkPoint>
{
    /// <inheritdoc />
    public override InkPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return ReadArray(ref reader);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            return ReadObject(ref reader);
        }

        throw new JsonException($"笔迹采样点应为数组 [x, y, pressure, tiltX, tiltY, timeMs]，实际为 {reader.TokenType}。");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, InkPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(Round(value.X, CvcannoCodec.CoordinateDecimals));
        writer.WriteNumberValue(Round(value.Y, CvcannoCodec.CoordinateDecimals));
        writer.WriteNumberValue(Round(value.Pressure, CvcannoCodec.SensorDecimals));
        writer.WriteNumberValue(Round(value.TiltX, CvcannoCodec.SensorDecimals));
        writer.WriteNumberValue(Round(value.TiltY, CvcannoCodec.SensorDecimals));
        writer.WriteNumberValue(value.TimeMs);
        writer.WriteEndArray();
    }

    /// <summary>读取数组形式：<c>[x, y, pressure, tiltX, tiltY, timeMs]</c>（缺项按默认值补齐）。</summary>
    private static InkPoint ReadArray(ref Utf8JsonReader reader)
    {
        var x = 0f;
        var y = 0f;
        var pressure = 0.5f;
        var tiltX = 0f;
        var tiltY = 0f;
        var timeMs = 0;

        var index = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (index)
            {
                case 0:
                    x = ReadSingle(ref reader);
                    break;
                case 1:
                    y = ReadSingle(ref reader);
                    break;
                case 2:
                    pressure = ReadSingle(ref reader);
                    break;
                case 3:
                    tiltX = ReadSingle(ref reader);
                    break;
                case 4:
                    tiltY = ReadSingle(ref reader);
                    break;
                case 5:
                    timeMs = ReadInt32(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }

            index++;
        }

        return new InkPoint(x, y, pressure, tiltX, tiltY, timeMs);
    }

    /// <summary>读取对象形式（<c>{"x":…,"y":…}</c>），兼容早期草稿与手工编辑过的文件。</summary>
    private static InkPoint ReadObject(ref Utf8JsonReader reader)
    {
        var x = 0f;
        var y = 0f;
        var pressure = 0.5f;
        var tiltX = 0f;
        var tiltY = 0f;
        var timeMs = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var name = reader.GetString();
            if (!reader.Read())
            {
                break;
            }

            switch (name)
            {
                case "x":
                case "X":
                    x = ReadSingle(ref reader);
                    break;
                case "y":
                case "Y":
                    y = ReadSingle(ref reader);
                    break;
                case "pressure":
                case "Pressure":
                    pressure = ReadSingle(ref reader);
                    break;
                case "tiltX":
                case "TiltX":
                    tiltX = ReadSingle(ref reader);
                    break;
                case "tiltY":
                case "TiltY":
                    tiltY = ReadSingle(ref reader);
                    break;
                case "timeMs":
                case "TimeMs":
                    timeMs = ReadInt32(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new InkPoint(x, y, pressure, tiltX, tiltY, timeMs);
    }

    /// <summary>读浮点（兼容数字与字符串两种写法）。</summary>
    private static float ReadSingle(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetSingle();
            case JsonTokenType.String:
                var text = reader.GetString();
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                throw new JsonException($"笔迹采样点数值无法解析：「{text}」。");
            case JsonTokenType.Null:
                return 0f;
            default:
                throw new JsonException($"笔迹采样点数值格式不正确（实际为 {reader.TokenType}）。");
        }
    }

    /// <summary>读整数毫秒（兼容数字与字符串两种写法）。</summary>
    private static int ReadInt32(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetInt32();
            case JsonTokenType.String:
                var text = reader.GetString();
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                throw new JsonException($"笔迹采样时间无法解析：「{text}」。");
            case JsonTokenType.Null:
                return 0;
            default:
                throw new JsonException($"笔迹采样时间格式不正确（实际为 {reader.TokenType}）。");
        }
    }

    /// <summary>
    /// 轻量舍入；非有限值（NaN / 无穷）写 0，避免生成非法 JSON。
    ///
    /// 注意必须先在 double 上放大再舍入，不能直接用 <c>MathF.Round(value, decimals, mode)</c>：
    /// float 放大 100 倍时本身就有半个 ulp 的误差，会把 1407.3049f 放大成 140730.5f，
    /// 被当成中点「远离零进位」而错误地舍入到 1407.31（误差 0.00513，超出 0.005 的舍入上限）。
    /// double 放大对页面坐标量级是精确的，舍入结果严格是最近的 2 / 3 位小数。
    /// </summary>
    private static float Round(float value, int decimals)
        => float.IsFinite(value)
            ? (float)Math.Round((double)value, decimals, MidpointRounding.AwayFromZero)
            : 0f;
}

/// <summary>
/// 课件哈希（PPT 文件 SHA256）格式规则：64 位十六进制、大小写不敏感、统一以小写保存。
/// 哈希是批注与课件之间唯一的绑定依据，格式不合法一律拒绝，避免串批注。
/// </summary>
internal static class AnnotationHashRules
{
    /// <summary>SHA256 十六进制字符串长度。</summary>
    public const int Sha256HexLength = 64;

    /// <summary>是否是合法的 64 位十六进制哈希（大小写均可）。</summary>
    public static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>规范化为小写十六进制（调用方需先校验格式）。</summary>
    public static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
