using System.Text;
using HtmlAgilityPack;

namespace KLaniBot.Platforms;

// Native mail reads pass HTML through a parser before a reviewer sees it. Scripts, styling, images and hidden
// preheaders do not enter the decision text; paragraph and table boundaries retain invoice amounts and identifiers.
internal static class MailHtmlText
{
    private static readonly HashSet<string> Suppressed = new(StringComparer.OrdinalIgnoreCase)
    {
        "head", "style", "script", "noscript", "img", "svg", "iframe", "object", "template"
    };
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "blockquote"
    };

    public static string Read(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var text = new StringBuilder();
        Append(document.DocumentNode, text);

        return ApplicationMailText.Readable(text.ToString());
    }

    private static void Append(HtmlNode node, StringBuilder text)
    {
        if (Suppressed.Contains(node.Name) || IsHidden(node))
        {
            return;
        }
        if (node.NodeType == HtmlNodeType.Text)
        {
            text.Append(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text));
            return;
        }
        bool block = Blocks.Contains(node.Name);
        if (block)
        {
            text.AppendLine();
        }
        foreach (HtmlNode child in node.ChildNodes)
        {
            Append(child, text);
        }
        text.Append(block ? "\n" : " ");
    }

    private static bool IsHidden(HtmlNode node)
    {
        string style = node.GetAttributeValue("style", "").Replace(" ", "").ToLowerInvariant();

        return node.Attributes.Contains("hidden") || node.GetAttributeValue("aria-hidden", "") == "true"
            || style.Contains("display:none", StringComparison.Ordinal)
            || style.Contains("visibility:hidden", StringComparison.Ordinal)
            || style.Contains("mso-hide:all", StringComparison.Ordinal);
    }
}
