using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace LocalLlm.Gui.Controls;

// Parse Markdown into native WinUI elements. No web scripts or raw HTML execute.
public sealed class MarkdownView(Action<Uri> openLink)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseAutoLinks().UseEmphasisExtras().DisableHtml().Build();
    private static Brush Surface => (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];

    public StackPanel Render(string markdown)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var block in Markdown.Parse(markdown, Pipeline)) panel.Children.Add(RenderBlock(block));
        return panel;
    }

    private UIElement RenderBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return Text(heading.Inline, heading.Level switch { 1 => 26, 2 => 22, 3 => 19, _ => 16 }, true);
            case ParagraphBlock paragraph: return Text(paragraph.Inline);
            case CodeBlock code:
                var content = new TextBlock { Text = code.Lines.ToString(), FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 13,
                    IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap };
                var codePanel = new StackPanel { Spacing = 6 };
                if (code is FencedCodeBlock fenced && !string.IsNullOrWhiteSpace(fenced.Info)) codePanel.Children.Add(new TextBlock { Text = fenced.Info, FontSize = 12, Opacity = 0.65 });
                codePanel.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
                return new Border { Child = codePanel, Background = Surface, CornerRadius = new CornerRadius(8), Padding = new Thickness(12) };
            case QuoteBlock quote:
                return new Border { Child = RenderContainer(quote), BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 4, 0, 4) };
            case ListBlock list:
                var items = new StackPanel { Spacing = 8 }; int number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", FontSize = 15 });
                    var body = RenderContainer(item); Grid.SetColumn(body, 1); row.Children.Add(body); items.Children.Add(row);
                }
                return items;
            case Table table:
                var grid = new Grid(); int rowIndex = 0;
                int columns = table.OfType<TableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
                for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                foreach (var row in table.OfType<TableRow>())
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); int column = 0;
                    foreach (var cell in row.OfType<TableCell>())
                    {
                        var cellPanel = RenderContainer(cell);
                        if (row.IsHeader) foreach (var view in cellPanel.Children.OfType<RichTextBlock>()) view.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        var border = new Border { Child = cellPanel, Padding = new Thickness(12, 8, 12, 8), BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], BorderThickness = new Thickness(0, 0, 0, 1), MinWidth = 80, MaxWidth = 360, Background = row.IsHeader ? Surface : null };
                        Grid.SetRow(border, rowIndex); Grid.SetColumn(border, column++); grid.Children.Add(border);
                    }
                    rowIndex++;
                }
                return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            case ThematicBreakBlock: return new Border { Height = 1, Background = Surface, Margin = new Thickness(0, 6, 0, 6) };
            case ContainerBlock container: return RenderContainer(container);
            default: return new TextBlock();
        }
    }

    private StackPanel RenderContainer(ContainerBlock container)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in container) panel.Children.Add(RenderBlock(block));
        return panel;
    }

    private RichTextBlock Text(ContainerInline? source, double fontSize = 15, bool bold = false)
    {
        var view = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = fontSize };
        if (bold) view.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var paragraph = new Paragraph();
        if (source != null) foreach (var inline in source) paragraph.Inlines.Add(RenderInline(inline));
        view.Blocks.Add(paragraph); return view;
    }

    private Microsoft.UI.Xaml.Documents.Inline RenderInline(Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal: return new Run { Text = literal.Content.ToString() };
            case LineBreakInline: return new LineBreak();
            case CodeInline code: return new Run { Text = code.Content, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 13 };
            case EmphasisInline emphasis:
                Span styled = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                foreach (var child in emphasis) styled.Inlines.Add(RenderInline(child));
                return styled;
            case LinkInline link:
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    var hyperlink = new Hyperlink();
                    foreach (var child in link) hyperlink.Inlines.Add(RenderInline(child));
                    hyperlink.Click += (_, _) => openLink(uri); return hyperlink;
                }
                var label = new Span(); foreach (var child in link) label.Inlines.Add(RenderInline(child)); return label;
            case AutolinkInline auto when Uri.TryCreate(auto.Url, UriKind.Absolute, out var autoUri) && autoUri.Scheme is "http" or "https":
                var autolink = new Hyperlink(); autolink.Inlines.Add(new Run { Text = auto.Url }); autolink.Click += (_, _) => openLink(autoUri); return autolink;
            case ContainerInline container:
                var span = new Span(); foreach (var child in container) span.Inlines.Add(RenderInline(child)); return span;
            default: return new Run { Text = inline.ToString() ?? "" };
        }
    }
}
