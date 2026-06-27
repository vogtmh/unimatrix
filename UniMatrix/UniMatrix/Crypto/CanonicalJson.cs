using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Windows.Data.Json;

namespace UniMatrix.Crypto
{
    /// <summary>
    /// Produces Matrix canonical JSON (https://spec.matrix.org/v1.11/appendices/#canonical-json):
    /// object keys sorted lexicographically by Unicode code point, no insignificant whitespace,
    /// integers without a fractional part. This exact byte sequence is what gets Ed25519-signed
    /// and verified, so correctness here underpins every signature (device keys, one-time keys,
    /// backup auth_data, SSSS).
    /// </summary>
    internal static class CanonicalJson
    {
        public static string Serialize(JsonObject obj)
        {
            var sb = new StringBuilder();
            WriteValue(sb, obj);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, IJsonValue value)
        {
            if (value == null) { sb.Append("null"); return; }
            switch (value.ValueType)
            {
                case JsonValueType.Object:
                    WriteObject(sb, value.GetObject());
                    break;
                case JsonValueType.Array:
                    WriteArray(sb, value.GetArray());
                    break;
                case JsonValueType.String:
                    // JsonValue.Stringify escapes the string exactly as JSON requires.
                    sb.Append(JsonValue.CreateStringValue(value.GetString()).Stringify());
                    break;
                case JsonValueType.Number:
                    WriteNumber(sb, value.GetNumber());
                    break;
                case JsonValueType.Boolean:
                    sb.Append(value.GetBoolean() ? "true" : "false");
                    break;
                case JsonValueType.Null:
                default:
                    sb.Append("null");
                    break;
            }
        }

        private static void WriteObject(StringBuilder sb, JsonObject obj)
        {
            var keys = new List<string>(obj.Keys);
            keys.Sort(StringComparer.Ordinal);
            sb.Append('{');
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonValue.CreateStringValue(keys[i]).Stringify());
                sb.Append(':');
                WriteValue(sb, obj[keys[i]]);
            }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, JsonArray arr)
        {
            sb.Append('[');
            for (int i = 0; i < arr.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteValue(sb, arr[i]);
            }
            sb.Append(']');
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            // Matrix canonical JSON only allows integers; emit whole numbers without a decimal.
            if (d == Math.Floor(d) && !double.IsInfinity(d) &&
                d >= long.MinValue && d <= long.MaxValue)
            {
                sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            }
        }
    }
}
