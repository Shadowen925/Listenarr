using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Common
{
    /// <summary>
    /// Expands <c>{...}</c> groups of a naming pattern. A group holds one or more tokens plus optional literal
    /// text (<c>{ - Series}</c>, <c>{ (PartNumber/PartCount)}</c>); when any token in the group is empty the whole
    /// group, literals included, is dropped. A bare empty token (<c>{Series}</c>) yields <see cref="EmptySentinel"/>
    /// so the caller can clean up the separators around it.
    /// </summary>
    internal static class NamingTokenEngine
    {
        public const string EmptySentinel = "__EMPTY_VAR__";

        private static readonly Regex GroupRegex = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
        private static readonly Regex LegacyName = new(@"^\w+(?::[^}]+)?$", RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<string, Regex> TokenRegexCache = new();

        public static string Expand(
            string pattern,
            IReadOnlyDictionary<string, object> variables,
            Func<string, string> sanitize,
            ILogger? logger = null)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in variables)
            {
                values[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            NamingTokenValues.AddDerived(values);
            var names = BuildNameMap(values.Keys);
            var tokenRegex = GetTokenRegex(names.Keys);

            return GroupRegex.Replace(pattern, group => ExpandGroup(group.Groups[1].Value, values, names, tokenRegex, sanitize, logger));
        }

        private static string ExpandGroup(
            string content,
            Dictionary<string, string> values,
            Dictionary<string, (string Display, string Key)> names,
            Regex tokenRegex,
            Func<string, string> sanitize,
            ILogger? logger)
        {
            var matches = tokenRegex.Matches(content);
            if (matches.Count == 0)
            {
                if (LegacyName.IsMatch(content))
                {
                    logger?.LogWarning("Variable {VariableName} not found in naming pattern", content);
                }

                return EmptySentinel;
            }

            var pure = matches.Count == 1 && matches[0].Value.Length == content.Length;
            var output = new StringBuilder();
            var cursor = 0;
            foreach (Match match in matches)
            {
                output.Append(Literal(content[cursor..match.Index]));
                var rendered = RenderToken(match, values, names);
                if (string.IsNullOrWhiteSpace(rendered))
                {
                    return pure ? EmptySentinel : string.Empty;
                }

                output.Append(sanitize(rendered));
                cursor = match.Index + match.Length;
            }

            output.Append(Literal(content[cursor..]));
            return output.ToString();
        }

        // A slash inside a group is text, not a folder separator.
        private static string Literal(string text) => text.Replace('/', '-').Replace('\\', '-');

        private static string RenderToken(
            Match match,
            Dictionary<string, string> values,
            Dictionary<string, (string Display, string Key)> names)
        {
            var matched = match.Groups["name"].Value;
            var (display, key) = names[NamingTokenValues.Normalize(matched)];
            values.TryGetValue(key, out var raw);
            raw ??= string.Empty;
            if (raw.Trim().Length == 0)
            {
                return string.Empty;
            }

            var format = match.Groups["fmt"].Success ? match.Groups["fmt"].Value : null;
            string rendered;
            if (key.Equals("BookSeries", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(format))
            {
                values.TryGetValue("SeriesPosition", out var position);
                rendered = "Book " + FormatValue(position ?? string.Empty, format, values);
            }
            else
            {
                rendered = FormatValue(raw, format, values);
            }

            if (display.Contains(' ') && !matched.Contains(' '))
            {
                var separator = matched.FirstOrDefault(c => c is '.' or '_' or '-');
                if (separator != default)
                {
                    rendered = rendered.Replace(' ', separator);
                }
            }

            var letters = matched.Where(char.IsLetter).ToArray();
            if (letters.Length > 0 && letters.All(char.IsUpper))
            {
                return rendered.ToUpperInvariant();
            }

            return letters.Length > 0 && letters.All(char.IsLower) ? rendered.ToLowerInvariant() : rendered;
        }

        private static string FormatValue(string value, string? format, Dictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(format))
            {
                return value;
            }

            if (format.Equals("smart", StringComparison.OrdinalIgnoreCase))
            {
                var width = values.TryGetValue("PartCount", out var count) && int.TryParse(count, out var total) && total > 0
                    ? total.ToString(CultureInfo.InvariantCulture).Length
                    : 1;
                return int.TryParse(value, out var smart) ? smart.ToString(new string('0', width), CultureInfo.InvariantCulture) : value;
            }

            if (int.TryParse(value, out var integer))
            {
                return integer.ToString(format, CultureInfo.InvariantCulture);
            }

            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                return value;
            }

            if (number != decimal.Truncate(number) && format.All(c => c == '0'))
            {
                var absolute = Math.Abs(number);
                var invariant = absolute.ToString(CultureInfo.InvariantCulture);
                var padded = decimal.Truncate(absolute).ToString(format, CultureInfo.InvariantCulture);
                return (number < 0 ? "-" : string.Empty) + padded + invariant[invariant.IndexOf('.')..];
            }

            return number.ToString(format, CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, (string Display, string Key)> BuildNameMap(IEnumerable<string> variableKeys)
        {
            var map = new Dictionary<string, (string, string)>();
            foreach (var key in variableKeys)
            {
                map[NamingTokenValues.Normalize(key)] = (key, key);
            }

            foreach (var (display, key) in NamingTokenValues.Catalog)
            {
                map[NamingTokenValues.Normalize(display)] = (display, key);
            }

            return map;
        }

        private static Regex GetTokenRegex(IEnumerable<string> normalizedNames)
        {
            var ordered = normalizedNames.OrderByDescending(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToList();
            return TokenRegexCache.GetOrAdd(string.Join('|', ordered), _ =>
            {
                var alternation = string.Join('|', ordered.Select(name =>
                    string.Join(@"[ ._\-]", name.Split(' ').Select(Regex.Escape))));
                return new Regex(
                    @"(?<![A-Za-z0-9])(?<name>" + alternation + @")(?::(?<fmt>[A-Za-z0-9#.]+))?(?![A-Za-z0-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            });
        }
    }
}
