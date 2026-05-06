using Newtonsoft.Json;
using System;
using System.Globalization;
using System.Linq;

namespace BanditMilitias.Infrastructure
{
    internal static class SafeTelemetry
    {
        private static readonly char[] DangerousFormulaPrefixes = { '=', '+', '-', '@' };

        public static string ToJson(object payload)
            => JsonConvert.SerializeObject(payload);

        public static string CsvRow(params object?[] values)
            => string.Join(",", values.Select(CsvCell));

        public static string CsvCell(object? value)
            => DelimitedCell(value, ',');

        public static string DelimitedCell(object? value, char delimiter)
        {
            string text = ToInvariantString(value)
                .Replace("\r", " ")
                .Replace("\n", " ");

            if (NeedsFormulaNeutralization(text))
            {
                text = "'" + text;
            }

            bool mustQuote = text.IndexOfAny(new[] { delimiter, '"', '\r', '\n' }) >= 0
                             || text.Length == 0
                             || char.IsWhiteSpace(text[0])
                             || char.IsWhiteSpace(text[text.Length - 1]);

            if (text.Contains("\""))
            {
                text = text.Replace("\"", "\"\"");
            }

            return mustQuote ? $"\"{text}\"" : text;
        }

        private static bool NeedsFormulaNeutralization(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            return DangerousFormulaPrefixes.Contains(text[0]) || text[0] == '\t';
        }

        private static string ToInvariantString(object? value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is string s)
            {
                return s;
            }

            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            return value.ToString() ?? string.Empty;
        }
    }
}
