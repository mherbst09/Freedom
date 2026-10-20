using System.Text.RegularExpressions;

namespace ViennaAtomicClock.Tutor;

public enum BlockKind { Paragraph, Heading, ListItem, Code, Quote, Rule }

public sealed record MarkdownBlock(BlockKind Kind, string Text, int Level = 0, string Language = "", string Marker = "");

public enum SpanKind { Text, Bold, Italic, Code }

public sealed record MarkdownSpan(SpanKind Kind, string Text);

/// <summary>Small Markdown parser for chat answers. Also copes with half-finished text while an answer streams in.</summary>
public static partial class MarkdownParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0) blocks.Add(new MarkdownBlock(BlockKind.Paragraph, string.Join(" ", paragraph).Trim()));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new List<string>();
                // An unclosed fence (answer still streaming) runs to the end of the text.
                while (++i < lines.Length && !lines[i].TrimStart().StartsWith(fence)) code.Add(lines[i]);
                blocks.Add(new MarkdownBlock(BlockKind.Code, string.Join("\n", code), Language: language));
                continue;
            }

            if (trimmed.Length == 0) { FlushParagraph(); continue; }

            if (HeadingPattern().Match(trimmed) is { Success: true } heading)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(BlockKind.Heading, heading.Groups[2].Value.Trim(), heading.Groups[1].Length));
                continue;
            }

            if (RulePattern().IsMatch(trimmed)) { FlushParagraph(); blocks.Add(new MarkdownBlock(BlockKind.Rule, "")); continue; }

            if (ListPattern().Match(line) is { Success: true } item)
            {
                FlushParagraph();
                var marker = item.Groups[2].Value;
                var ordered = char.IsDigit(marker[0]);
                blocks.Add(new MarkdownBlock(BlockKind.ListItem, item.Groups[3].Value.Trim(), item.Groups[1].Value.Length / 2, Marker: ordered ? marker : "•"));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(BlockKind.Quote, trimmed.TrimStart('>').Trim()));
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>Inline formatting: **bold**, *italic* / _italic_, `code`. Links are shown as their text.</summary>
    public static IReadOnlyList<MarkdownSpan> Inline(string text)
    {
        text = LinkPattern().Replace(text, "$1");
        var spans = new List<MarkdownSpan>();
        var position = 0;
        foreach (Match m in InlinePattern().Matches(text))
        {
            if (m.Index > position) spans.Add(new MarkdownSpan(SpanKind.Text, text[position..m.Index]));
            if (m.Groups["code"].Success) spans.Add(new MarkdownSpan(SpanKind.Code, m.Groups["code"].Value));
            else if (m.Groups["bold"].Success) spans.Add(new MarkdownSpan(SpanKind.Bold, m.Groups["bold"].Value));
            else if (m.Groups["bold2"].Success) spans.Add(new MarkdownSpan(SpanKind.Bold, m.Groups["bold2"].Value));
            else if (m.Groups["italic"].Success) spans.Add(new MarkdownSpan(SpanKind.Italic, m.Groups["italic"].Value));
            else spans.Add(new MarkdownSpan(SpanKind.Italic, m.Groups["italic2"].Value));
            position = m.Index + m.Length;
        }
        if (position < text.Length) spans.Add(new MarkdownSpan(SpanKind.Text, text[position..]));
        return spans;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(\*\s*\*\s*\*|-\s*-\s*-|_\s*_\s*_)[\s*_-]*$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|__(?<bold2>[^_]+)__|(?<![\w*])\*(?<italic>[^*\s][^*]*)\*(?!\w)|(?<![\w_])_(?<italic2>[^_\s][^_]*)_(?!\w)")]
    private static partial Regex InlinePattern();
}

public enum TokenKind { Plain, Keyword, Type, String, Comment, Number }

/// <summary>Lightweight syntax highlighting for the languages that come up at school.</summary>
public static partial class CodeHighlighter
{
    private static readonly HashSet<string> CSharpKeywords = new(
        ("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event " +
         "explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null " +
         "object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string " +
         "struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while var async await get set " +
         "init record yield nameof when where with dynamic global partial value required file").Split(' '));

    private static readonly HashSet<string> SqlKeywords = new(
        ("select from where insert into values update set delete create table drop alter add primary key foreign references join inner left " +
         "right outer on group by order having distinct as and or not null is in between like limit count sum avg min max union all " +
         "index view int integer varchar char text date datetime boolean default unique check constraint asc desc").Split(' '),
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<(TokenKind Kind, string Text)> Tokenize(string code, string language)
    {
        var lang = language.ToLowerInvariant();
        var sql = lang is "sql" or "mysql" or "sqlite" or "tsql";
        var keywords = sql ? SqlKeywords : CSharpKeywords;
        var lineComment = sql ? "--" : lang is "python" or "py" or "bash" or "sh" or "powershell" or "ps1" ? "#" : "//";
        var plainLanguage = lang is "text" or "txt" or "markdown" or "md" or "plantuml" or "mermaid" or "json" or "xml" or "output";

        var tokens = new List<(TokenKind, string)>();
        if (plainLanguage)
        {
            tokens.Add((TokenKind.Plain, code));
            return tokens;
        }

        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (code.AsSpan(i).StartsWith(lineComment))
            {
                var end = code.IndexOf('\n', i);
                end = end < 0 ? code.Length : end;
                tokens.Add((TokenKind.Comment, code[i..end]));
                i = end;
            }
            else if (!sql && code.AsSpan(i).StartsWith("/*"))
            {
                var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 2;
                tokens.Add((TokenKind.Comment, code[i..end]));
                i = end;
            }
            else if (c is '"' or '\'')
            {
                var end = i + 1;
                while (end < code.Length && code[end] != c && code[end] != '\n')
                    end += code[end] == '\\' && !sql ? 2 : 1;
                end = Math.Min(code.Length, end + 1);
                tokens.Add((TokenKind.String, code[i..end]));
                i = end;
            }
            else if (char.IsDigit(c))
            {
                var end = i;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '.' || code[end] == '_')) end++;
                tokens.Add((TokenKind.Number, code[i..end]));
                i = end;
            }
            else if (char.IsLetter(c) || c == '_' || c == '@')
            {
                var end = i + 1;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_')) end++;
                var word = code[i..end];
                var kind = keywords.Contains(word) ? TokenKind.Keyword
                    : !sql && char.IsUpper(word[0]) ? TokenKind.Type
                    : TokenKind.Plain;
                tokens.Add((kind, word));
                i = end;
            }
            else
            {
                var end = i + 1;
                while (end < code.Length && !char.IsLetterOrDigit(code[end]) && code[end] is not ('"' or '\'' or '_' or '@' or '/' or '-' or '#')) end++;
                tokens.Add((TokenKind.Plain, code[i..end]));
                i = end;
            }
        }
        return tokens;
    }
}
