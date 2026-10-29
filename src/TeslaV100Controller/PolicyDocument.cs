using System.Text.RegularExpressions;

namespace TeslaV100Controller;

// Operate only on owned Shutdown entries; retain other sections and script indexes.
internal static class PolicyDocument
{
    private static readonly Regex Section = new(@"(?ims)^\[Shutdown\][ \t]*\r?\n(?<body>.*?)(?=^\[|\z)");
    public static string Update(string text, Func<string, string, bool> owned, string? exe, string arguments)
    {
        var match = Section.Match(text);
        string body = match.Groups["body"].Value;
        foreach (Match entry in Regex.Matches(body, @"(?im)^(\d+)CmdLine=([^\r\n]*)"))
        {
            string index = entry.Groups[1].Value;
            string args = Regex.Match(body, $@"(?im)^{index}Parameters=([^\r\n]*)").Groups[1].Value;
            if (owned(entry.Groups[2].Value.Trim(), args.Trim()))
                body = Regex.Replace(body, $@"(?im)^{index}(CmdLine|Parameters)=[^\r\n]*(\r?\n|$)", "");
        }
        if (exe is not null)
        {
            var used = Regex.Matches(body, @"(?im)^(\d+)CmdLine=").Select(x => int.Parse(x.Groups[1].Value)).ToHashSet();
            int n = 0;
            while (used.Contains(n)) n++;
            body = body.TrimEnd() + $"\r\n{n}CmdLine={exe}\r\n{n}Parameters={arguments}\r\n";
        }
        string replacement = string.IsNullOrWhiteSpace(body) ? "" : "[Shutdown]\r\n" + body.TrimStart('\r', '\n');
        return match.Success ? text.Remove(match.Index, match.Length).Insert(match.Index, replacement)
                             : text.TrimEnd() + (replacement.Length == 0 ? "" : "\r\n" + replacement);
    }
}
