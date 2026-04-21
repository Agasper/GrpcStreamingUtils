using System.Collections.Concurrent;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Niarru.GrpcStreamingUtils.Logging;

public static class SensitiveDataRedactor
{
    private const string RedactedText = "[REDACTED]";
    private const int MaxLength = 16384;

    public static bool SkipDefaultFields { get; set; } = true;

    private static readonly string[] SensitiveKeywords =
    {
        "token", "secret", "password", "credential", "ticket"
    };

    private static readonly string[] SensitiveKeywordExceptions =
    {
        "tokenType", "token_type"
    };

    private static readonly ConcurrentDictionary<Type, IReadOnlyList<FieldDescriptor>> DescriptorCache = new();

    public static string Redact(object? message)
    {
        if (message == null)
            return "null";

        if (message is IMessage protoMessage)
            return RedactProtobufMessage(protoMessage);

        var result = message.ToString() ?? "null";
        return result.Length >= MaxLength ? "[TOO LARGE]" : result;
    }

    internal static bool IsSensitiveField(string fieldName)
    {
        foreach (var exception in SensitiveKeywordExceptions)
        {
            if (fieldName.Equals(exception, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var keyword in SensitiveKeywords)
        {
            if (fieldName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string RedactProtobufMessage(IMessage message)
    {
        var messageType = message.GetType();
        var fields = DescriptorCache.GetOrAdd(
            messageType,
            _ => message.Descriptor.Fields.InDeclarationOrder().ToList());

        var sb = new StringBuilder();
        sb.Append("{ ");

        var first = true;
        foreach (var field in fields)
        {
            var value = field.Accessor.GetValue(message);

            if (SkipDefaultFields)
            {
                if (value == null || IsDefaultValue(value, field))
                    continue;

                if (field.FieldType == FieldType.Message && !field.IsRepeated && !field.IsMap
                    && value is IMessage nested && IsEmptyMessage(nested))
                    continue;
            }

            if (!first) sb.Append(", ");
            first = false;

            sb.Append($"\"{field.JsonName}\": ");

            if (IsSensitiveField(field.JsonName))
            {
                sb.Append(value != null && !IsDefaultValue(value, field)
                    ? $"\"{RedactedText}\""
                    : "\"\"");
            }
            else if (value == null)
            {
                sb.Append("null");
            }
            else if (field.FieldType == FieldType.Message && !field.IsRepeated && !field.IsMap && value is IMessage nestedMsg)
            {
                sb.Append(RedactProtobufMessage(nestedMsg));
            }
            else if (field.FieldType is FieldType.String or FieldType.Enum)
            {
                sb.Append($"\"{value}\"");
            }
            else
            {
                sb.Append(value);
            }
        }

        sb.Append(" }");

        var result = sb.ToString();
        return result.Length >= MaxLength ? "[TOO LARGE]" : result;
    }

    private static bool IsEmptyMessage(IMessage message)
    {
        foreach (var f in message.Descriptor.Fields.InDeclarationOrder())
        {
            var v = f.Accessor.GetValue(message);
            if (v != null && !IsDefaultValue(v, f))
                return false;
        }
        return true;
    }

    private static bool IsDefaultValue(object value, FieldDescriptor field)
    {
        return field.FieldType switch
        {
            FieldType.String => string.IsNullOrEmpty(value as string),
            FieldType.Bool => !(bool)value,
            FieldType.Enum => Convert.ToInt32(value) == 0,
            FieldType.Int32 or FieldType.Int64 or FieldType.UInt32 or FieldType.UInt64
                or FieldType.SInt32 or FieldType.SInt64 or FieldType.Fixed32 or FieldType.Fixed64
                or FieldType.SFixed32 or FieldType.SFixed64 => Convert.ToInt64(value) == 0,
            _ => false
        };
    }
}
