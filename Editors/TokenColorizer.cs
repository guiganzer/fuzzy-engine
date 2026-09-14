using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace PostgresCommandExecuter.Editors
{
    /// <summary>
    /// A presentation-only colorizer for AvalonEdit. It never mutates the
    /// document, so the caret, undo stack and selection stay intact while the
    /// user types. The editor calls it only for lines that are being rendered.
    /// </summary>
    public sealed class TokenColorizer : DocumentColorizingTransformer
    {
        private readonly Func<TokenStyleKind, Brush> brushResolver;
        private IList<TokenStyleSpan> spans = new List<TokenStyleSpan>();

        public TokenColorizer(Func<TokenStyleKind, Brush> brushResolver)
        {
            if (brushResolver == null) throw new ArgumentNullException("brushResolver");
            this.brushResolver = brushResolver;
        }

        public void SetSpans(IList<TokenStyleSpan> newSpans)
        {
            spans = newSpans ?? new List<TokenStyleSpan>();
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            int lineStart = line.Offset;
            int lineEnd = line.EndOffset;
            int first = FindFirstIntersectingSpan(lineStart);

            for (int index = first; index < spans.Count; index++)
            {
                TokenStyleSpan span = spans[index];
                if (span.Start >= lineEnd) break;
                if (span.End <= lineStart) continue;

                Brush brush = brushResolver(span.Kind);
                if (brush == null) continue;

                int start = Math.Max(lineStart, span.Start);
                int end = Math.Min(lineEnd, span.End);
                bool bold = span.Emphasized;
                ChangeLinePart(start, end, delegate(VisualLineElement element)
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    if (bold)
                    {
                        Typeface current = element.TextRunProperties.Typeface;
                        element.TextRunProperties.SetTypeface(new Typeface(
                            current.FontFamily,
                            current.Style,
                            FontWeights.SemiBold,
                            current.Stretch));
                    }
                });
            }
        }

        private int FindFirstIntersectingSpan(int offset)
        {
            int low = 0;
            int high = spans.Count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (spans[middle].End <= offset) low = middle + 1;
                else high = middle;
            }

            return low;
        }
    }

    public enum TokenStyleKind
    {
        Keyword,
        Type,
        Function,
        String,
        Number,
        Comment,
        Parameter,
        PropertyName,
        Literal,
        Punctuation,
        Invalid
    }

    public sealed class TokenStyleSpan
    {
        public TokenStyleSpan(int start, int length, TokenStyleKind kind, bool emphasized)
        {
            Start = Math.Max(0, start);
            Length = Math.Max(0, length);
            Kind = kind;
            Emphasized = emphasized;
        }

        public int Start { get; private set; }

        public int Length { get; private set; }

        public int End { get { return Start + Length; } }

        public TokenStyleKind Kind { get; private set; }

        public bool Emphasized { get; private set; }
    }
}
