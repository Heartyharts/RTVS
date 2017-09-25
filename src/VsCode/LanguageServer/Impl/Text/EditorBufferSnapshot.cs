﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.Common.Core;
using Microsoft.Common.Core.Diagnostics;
using Microsoft.Languages.Core.Text;
using Microsoft.Languages.Editor.Text;

namespace Microsoft.R.LanguageServer.Text {
    internal sealed class EditorBufferSnapshot : IEditorBufferSnapshot {
        private readonly object _lock = new object();
        private readonly string _content;
        private TextRangeCollection<EditorLine> _lines;

        public EditorBufferSnapshot(IEditorBuffer editorBuffer, string content, int version) {
            EditorBuffer = editorBuffer;
            _content = content;
            Version = version;
        }

        #region IEditorBufferSnapshot
        public int Length => _content.Length;
        public char this[int position] => _content[position];
        public string GetText() => _content;
        public string GetText(ITextRange range) => _content.Substring(range.Start, range.Length);

        public int IndexOf(string text, int startPosition, bool ignoreCase)
            => _content.IndexOf(text, startPosition, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        public int IndexOf(char ch, int startPosition) => _content.IndexOf(ch, startPosition);
        public int IndexOf(char ch, ITextRange range) => _content.IndexOf(ch, range.Start, range.Length);

        public int IndexOf(string text, ITextRange range, bool ignoreCase)
            => _content.IndexOf(text, range.Start, range.Length, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        public bool CompareTo(int position, int length, string text, bool ignoreCase)
            => string.Compare(_content, position, text, 0, length, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) == 0;

        public ITextProvider Clone() => new TextStream(new string(_content.ToCharArray()));
        public int Version { get; }
        public T As<T>() where T : class => throw new NotSupportedException();
        public IEditorBuffer EditorBuffer { get; }

        public int LineCount {
            get {
                MakeLinesData();
                return _lines.Count;
            }
        }

        public IEditorLine GetLineFromPosition(int position) {
            MakeLinesData();
            var lineIndex = position == Length 
                ? _lines.Count - 1
                : _lines.GetItemContaining(position);

            return lineIndex < 0 ? null : _lines[lineIndex];
        }

        public IEditorLine GetLineFromLineNumber(int lineNumber) {
            Check.ArgumentOutOfRange(nameof(lineNumber), () => lineNumber < 0 || lineNumber >= LineCount);
            return _lines[lineNumber];
        }

        public int GetLineNumberFromPosition(int position) {
            Check.ArgumentOutOfRange(nameof(position), () => position < 0 || position >= _content.Length);
            MakeLinesData();
            return _lines.GetItemContaining(position);
        }

        public ITrackingTextRange CreateTrackingRange(ITextRange range) => new TrackingTextRange(range);
        #endregion

        private void MakeLinesData() {
            if (_lines != null) {
                return;
            }

            lock (_lock) {
                var lines = new List<EditorLine>();
                var lineStart = 0;

                for (var i = 0; i < _content.Length; i++) {
                    var ch = _content[i];
                    if (ch.IsLineBreak()) {
                        var lineBreakLength = 1;
                        if (ch == '\r' && i + 1 < _content.Length && _content[i + 1] == '\n') {
                            i++;
                            lineBreakLength++;
                        } else if (ch == '\n' && i + 1 < _content.Length && _content[i + 1] == '\r') {
                            i++;
                            lineBreakLength++;
                        }
                        lines.Add(new EditorLine(this, lineStart, i + 1 - lineStart, lineBreakLength, lines.Count));
                        lineStart = i + 1;
                    }
                }
                lines.Add(new EditorLine(this, lineStart, _content.Length - lineStart, 0, lines.Count));
                _lines = new TextRangeCollection<EditorLine>(lines);
            }
        }

#pragma warning disable 67
        public event EventHandler<TextChangeEventArgs> OnTextChange;
    }
}
