using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ClaudeMobileTerminal.Backend.Services;

public class TelegramMarkdownConverter
{
    private readonly MarkdownPipeline _pipeline;
    
    public TelegramMarkdownConverter()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseEmphasisExtras()
            .Build();
    }
    
    public string ConvertToTelegramMarkdownV2(string markdown)
    {
        var document = Markdown.Parse(markdown, _pipeline);
        var renderer = new TelegramMarkdownV2Renderer();
        return renderer.Render(document);
    }
}

public class TelegramMarkdownV2Renderer
{
    private readonly StringBuilder _builder = new();
    private bool _isInCodeBlock = false;
    private bool _isInInlineCode = false;
    
    public string Render(MarkdownDocument document)
    {
        foreach (var block in document)
        {
            RenderBlock(block);
        }
        return _builder.ToString();
    }
    
    private void RenderBlock(Block block)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                RenderParagraph(paragraph);
                break;
            case CodeBlock codeBlock:
                RenderCodeBlock(codeBlock);
                break;
            case HeadingBlock heading:
                RenderHeading(heading);
                break;
            case ListBlock list:
                RenderList(list);
                break;
            case QuoteBlock quote:
                RenderQuote(quote);
                break;
            case BlankLineBlock:
                _builder.AppendLine();
                break;
            default:
                // For other block types, try to render their inline content
                if (block is LeafBlock leafBlock && leafBlock.Inline != null)
                {
                    RenderInline(leafBlock.Inline);
                    _builder.AppendLine();
                }
                break;
        }
    }
    
    private void RenderParagraph(ParagraphBlock paragraph)
    {
        if (paragraph.Inline != null)
        {
            RenderInline(paragraph.Inline);
        }
        
        // Add newline after paragraph unless it's the last block
        if (paragraph.Parent != null && paragraph.Parent.LastChild != paragraph)
        {
            _builder.AppendLine();
        }
    }
    
    private void RenderCodeBlock(CodeBlock codeBlock)
    {
        _isInCodeBlock = true;
        
        var code = GetCodeBlockContent(codeBlock);
        
        _builder.Append("```");
        
        // Add language if available
        if (codeBlock is FencedCodeBlock fenced && !string.IsNullOrEmpty(fenced.Info))
        {
            _builder.Append(fenced.Info);
        }
        
        _builder.AppendLine();
        
        // Only escape backticks and backslashes in code blocks
        code = code.Replace("\\", "\\\\").Replace("`", "\\`");
        _builder.Append(code);
        
        // Ensure code block ends with newline before closing ```
        if (!code.EndsWith('\n'))
        {
            _builder.AppendLine();
        }
        
        _builder.Append("```");
        
        // Add newline after code block unless it's the last block
        if (codeBlock.Parent != null && codeBlock.Parent.LastChild != codeBlock)
        {
            _builder.AppendLine();
        }
        
        _isInCodeBlock = false;
    }
    
    private void RenderHeading(HeadingBlock heading)
    {
        // Render heading as bold text
        _builder.Append('*');
        if (heading.Inline != null)
        {
            RenderInline(heading.Inline);
        }
        _builder.Append('*');
        _builder.AppendLine();
    }
    
    private void RenderList(ListBlock list)
    {
        foreach (var item in list)
        {
            if (item is ListItemBlock listItem)
            {
                _builder.Append(list.IsOrdered ? "1\\. " : "• ");
                
                var isFirst = true;
                foreach (var child in listItem)
                {
                    if (!isFirst)
                    {
                        _builder.Append("  "); // Indent continuation
                    }
                    RenderBlock(child);
                    isFirst = false;
                }
            }
        }
    }
    
    private void RenderQuote(QuoteBlock quote)
    {
        foreach (var child in quote)
        {
            _builder.Append("> ");
            RenderBlock(child);
        }
    }
    
    private void RenderInline(Inline inline)
    {
        var current = inline;
        while (current != null)
        {
            switch (current)
            {
                case LiteralInline literal:
                    RenderLiteral(literal);
                    break;
                case EmphasisInline emphasis:
                    RenderEmphasis(emphasis);
                    break;
                case CodeInline code:
                    RenderCodeInline(code);
                    break;
                case LineBreakInline:
                    _builder.AppendLine();
                    break;
                case LinkInline link:
                    RenderLink(link);
                    break;
                default:
                    // For unknown inline types, try to render their content
                    if (current is ContainerInline container && container.FirstChild != null)
                    {
                        RenderInline(container.FirstChild);
                    }
                    break;
            }
            current = current.NextSibling;
        }
    }
    
    private void RenderLiteral(LiteralInline literal)
    {
        var text = literal.Content.ToString();
        
        // Different escaping rules for code blocks vs regular text
        if (_isInCodeBlock || _isInInlineCode)
        {
            // In code blocks, only escape backticks and backslashes
            text = text.Replace("\\", "\\\\").Replace("`", "\\`");
        }
        else
        {
            // In regular text, escape all special characters
            text = EscapeForTelegramMarkdownV2(text);
        }
        
        _builder.Append(text);
    }
    
    private void RenderEmphasis(EmphasisInline emphasis)
    {
        // Telegram uses single * for bold and _ for italic
        var delimiter = emphasis.DelimiterCount == 2 ? "*" : "_";
        
        _builder.Append(delimiter);
        if (emphasis.FirstChild != null)
        {
            RenderInline(emphasis.FirstChild);
        }
        _builder.Append(delimiter);
    }
    
    private void RenderCodeInline(CodeInline code)
    {
        _isInInlineCode = true;
        _builder.Append('`');
        
        var content = code.Content;
        // Only escape backticks and backslashes in inline code
        content = content.Replace("\\", "\\\\").Replace("`", "\\`");
        _builder.Append(content);
        
        _builder.Append('`');
        _isInInlineCode = false;
    }
    
    private void RenderLink(LinkInline link)
    {
        // For now, just render the link text or URL
        if (link.FirstChild != null)
        {
            RenderInline(link.FirstChild);
        }
        else if (!string.IsNullOrEmpty(link.Url))
        {
            var url = EscapeForTelegramMarkdownV2(link.Url);
            _builder.Append(url);
        }
    }
    
    private static string GetCodeBlockContent(CodeBlock codeBlock)
    {
        var lines = codeBlock.Lines;
        var builder = new StringBuilder();
        
        foreach (var line in lines)
        {
            builder.AppendLine(line.ToString());
        }
        
        return builder.ToString().TrimEnd('\n', '\r');
    }
    
    private static string EscapeForTelegramMarkdownV2(string text)
    {
        // Escape backslash first to avoid double-escaping
        text = text.Replace("\\", "\\\\");
        
        // Escape all special characters as per Telegram MarkdownV2 spec
        var charsToEscape = "_*[]()~`>#+-=|{}.!";
        foreach (var c in charsToEscape)
        {
            text = text.Replace(c.ToString(), $"\\{c}");
        }
        
        return text;
    }
}