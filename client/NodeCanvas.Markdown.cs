using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AgentOrange;

/// <summary>
/// Markdown pasted into a Rich Text node, such as a part of a design document, comes in as what it
/// means: headings, lists, tick boxes, tables and marked words, made the same way the buttons and
/// typed Markdown make them (<see cref="MarkdownText"/>). Words that don't look like Markdown paste as
/// they always did.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const double DocumentWidth = 560;

    // A node keeps an empty line between blocks, as typing one would, rather than space around them.
    private static MarkdownText.Look NodeLook => new(TextSize, HeadingSizes, LinkColour, EmptyLineBetween: true, BlockMargin: new Thickness(0));

    // Takes the paste over when it's Markdown, and leaves anything else to the box as before.
    private void PasteMarkdown(NodeView node, RichTextBox rich, DataObjectPastingEventArgs pasting)
    {
        if (!pasting.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true)) return;
        if (pasting.SourceDataObject.GetData(DataFormats.UnicodeText, true) is not string text || !MarkdownText.LooksLikeMarkdown(text)) return;
        pasting.CancelCommand();

        // A part of a document would be a long thin strip in a node as narrow as a new one.
        if ((node.Data.Width ?? TextWidth) <= TextWidth && (text.Length > 600 || text.Contains("\n|", StringComparison.Ordinal)))
        {
            node.Data = node.Data with { Width = DocumentWidth };
            node.Box.Width = DocumentWidth;
        }

        // In the node's own look, or it would take a new document's: black, serif, justified and spaced out.
        var made = new FlowDocument
        {
            FontFamily = rich.Document.FontFamily,
            FontSize = TextSize,
            Foreground = rich.Foreground,
            TextAlignment = TextAlignment.Left,
        };
        made.Blocks.AddRange(MarkdownText.ToBlocks(text, NodeLook));
        using var xaml = new MemoryStream();
        new TextRange(made.ContentStart, made.ContentEnd).Save(xaml, DataFormats.Xaml);
        xaml.Position = 0;

        _formatting = true;
        rich.BeginChange();
        try
        {
            rich.Selection.Load(xaml, DataFormats.Xaml);
            rich.CaretPosition = rich.Selection.End;
        }
        finally
        {
            rich.EndChange();
            _formatting = false;
        }
    }
}
