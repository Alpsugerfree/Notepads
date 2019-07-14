﻿
namespace Notepads.Controls.TextEditor
{
    using Notepads.Services;
    using Notepads.Utilities;
    using System;
    using System.Text;
    using System.Threading.Tasks;
    using Windows.ApplicationModel.DataTransfer;
    using Windows.Storage;
    using Windows.System;
    using Windows.UI.Core;
    using Windows.UI.Text;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;
    using Windows.UI.Xaml.Input;
    using Windows.UI.Xaml.Media;

    public class TextEditor : RichEditBox
    {
        public StorageFile EditingFile { get; set; }

        public Encoding Encoding { get; set; }

        public LineEnding LineEnding { get; set; }

        public bool Saved { get; set; }

        public event EventHandler<KeyRoutedEventArgs> OnSetClosingKeyDown;

        private string[] _documentLinesCache;

        public TextEditor()
        {
            DefaultStyleKey = nameof(TextEditor);
            Style = (Style)Application.Current.Resources[nameof(TextEditor)];
            IsSpellCheckEnabled = false;
            TextWrapping = EditorSettingsService.EditorDefaultTextWrapping;
            FontFamily = new FontFamily(EditorSettingsService.EditorFontFamily);
            FontSize = EditorSettingsService.EditorFontSize;
            SelectionHighlightColor = Application.Current.Resources["SystemControlForegroundAccentBrush"] as SolidColorBrush;
            SelectionHighlightColorWhenNotFocused = Application.Current.Resources["SystemControlForegroundAccentBrush"] as SolidColorBrush;
            SelectionFlyout = null;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            HandwritingView.BorderThickness = new Thickness(0);
            ContextFlyout = new TextEditorContextFlyout(this);
            TextChanging += TextEditor_TextChanging;
            Paste += async (sender, args) => await PastePlainTextFromWindowsClipboard(args);
            PointerWheelChanged += OnPointerWheelChanged;

            SetDefaultTabStop(FontFamily, FontSize);

            EditorSettingsService.OnFontFamilyChanged += (sender, fontFamily) =>
            {
                FontFamily = new FontFamily(fontFamily);
                SetDefaultTabStop(FontFamily, FontSize);
            };
            EditorSettingsService.OnFontSizeChanged += (sender, fontSize) =>
            {
                FontSize = fontSize;
                SetDefaultTabStop(FontFamily, FontSize);
            };
            EditorSettingsService.OnDefaultTextWrappingChanged += (sender, textWrapping) => { TextWrapping = textWrapping; };
            ThemeSettingsService.OnAccentColorChanged += (sender, color) =>
            {
                SelectionHighlightColor = Application.Current.Resources["SystemControlForegroundAccentBrush"] as SolidColorBrush;
                SelectionHighlightColorWhenNotFocused = Application.Current.Resources["SystemControlForegroundAccentBrush"] as SolidColorBrush;
            };
        }

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var ctrl = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control);
            var alt = Window.Current.CoreWindow.GetKeyState(VirtualKey.Menu);
            var shift = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift);

            if (ctrl.HasFlag(CoreVirtualKeyStates.Down) &&
                !alt.HasFlag(CoreVirtualKeyStates.Down) &&
                !shift.HasFlag(CoreVirtualKeyStates.Down))
            {
                var mouseWheelDelta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
                if (mouseWheelDelta > 0)
                {
                    SetDefaultTabStop(FontFamily, FontSize + 1);
                    FontSize += 1;
                }
                else if (mouseWheelDelta < 0)
                {
                    if (!(FontSize > 4)) return;
                    SetDefaultTabStop(FontFamily, FontSize - 1);
                    FontSize -= 1;
                }
            }
        }

        private void SetDefaultTabStop(FontFamily font, double fontSize)
        {
            Document.DefaultTabStop = (float)FontUtility.GetTextSize(font, fontSize, "text").Width;
            TextDocument.DefaultTabStop = (float)FontUtility.GetTextSize(font, fontSize, "text").Width;
        }

        private void TextEditor_TextChanging(RichEditBox sender, RichEditBoxTextChangingEventArgs args)
        {
            if (args.IsContentChanging)
            {
                _documentLinesCache = null;
            }
        }

        public async Task PastePlainTextFromWindowsClipboard(TextControlPasteEventArgs e)
        {
            if (e != null)
            {
                e.Handled = true;
            }

            if (!Document.CanPaste()) return;

            var dataPackageView = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!dataPackageView.Contains(StandardDataFormats.Text)) return;
            try
            {
                var text = await dataPackageView.GetTextAsync();
                Document.Selection.SetText(TextSetOptions.None, text);
                Document.Selection.StartPosition = Document.Selection.EndPosition;
            }
            catch (Exception)
            {
                // ignore
            }
        }

        //public static string GetTextWithDefaultTabIndentation(string text)
        //{
        //    if (EditorSettingsService.EditorDefaultTabIndents == -1) return text;
        //    var tabStr = new string(' ', EditorSettingsService.EditorDefaultTabIndents);
        //    text = text.Replace("\t", tabStr);
        //    return text;
        //}

        public async Task SaveToFile(StorageFile file)
        {
            Encoding encoding = Encoding ?? new UTF8Encoding(false);
            var text = GetText();
            text = FixLineEnding(text, LineEnding);
            await FileSystemUtility.WriteToFile(text, encoding, file);
            EditingFile = file;
            Encoding = encoding;
            Saved = true;
        }

        public string GetText()
        {
            Document.GetText(TextGetOptions.None, out var text);
            // RichEditBox's Document.GetText() method by default append an extra '\r' at end of the text string
            // We need to trim it before proceeding
            return TrimText(text); 
        }

        public string GetContentForSharing()
        {
            string content;

            if (Document.Selection.StartPosition == Document.Selection.EndPosition)
            {
                content = GetText();
            }
            else
            {
                content = Document.Selection.Text;
            }

            return content;
        }

        private static int IndexOfWholeWord(string target, int startIndex, string value, StringComparison comparison)
        {
            int pos = startIndex;
            while (pos < target.Length && (pos = target.IndexOf(value, pos, comparison)) != -1)
            {
                bool startBoundary = true;
                if (pos > 0)
                    startBoundary = !Char.IsLetterOrDigit(target[pos - 1]);

                bool endBoundary = true;
                if (pos + value.Length < target.Length)
                    endBoundary = !Char.IsLetterOrDigit(target[pos + value.Length]);

                if (startBoundary && endBoundary)
                    return pos;

                pos++;
            }
            return -1;
        }

        public bool FindNextAndReplace(string searchText, string replaceText, bool matchCase, bool matchWholeWord)
        {
            if (FindNextAndSelect(searchText, matchCase, matchWholeWord))
            {
                Document.Selection.SetText(TextSetOptions.None, replaceText);
                return true;
            }

            return false;
        }

        public bool FindAndReplaceAll(string searchText, string replaceText, bool matchCase, bool matchWholeWord)
        {
            var found = false;

            var pos = 0;
            var searchTextLength = searchText.Length;
            var replaceTextLength = replaceText.Length;

            var text = GetText();

            StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            pos = matchWholeWord ? IndexOfWholeWord(text, pos, searchText, comparison) : text.IndexOf(searchText, pos, comparison);

            while (pos != -1)
            {
                found = true;
                text = text.Remove(pos, searchTextLength).Insert(pos, replaceText);
                pos += replaceTextLength;
                pos = matchWholeWord ? IndexOfWholeWord(text, pos, searchText, comparison) : text.IndexOf(searchText, pos, comparison);
            }

            if (found)
            {
                SetText(text);
                Document.Selection.StartPosition = Int32.MaxValue;
                Document.Selection.EndPosition = Document.Selection.StartPosition;
            }

            return found;
        }

        public bool FindNextAndSelect(string searchText, bool matchCase, bool matchWholeWord, bool stopAtEof = true)
        {
            if (string.IsNullOrEmpty(searchText))
            {
                return false;
            }

            var text = GetText();

            StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            var index = matchWholeWord ? IndexOfWholeWord(text, Document.Selection.EndPosition, searchText, comparison) : text.IndexOf(searchText, Document.Selection.EndPosition, comparison);

            if (index != -1)
            {
                Document.Selection.StartPosition = index;
                Document.Selection.EndPosition = index + searchText.Length;
            }
            else
            {
                if (!stopAtEof)
                {
                    index = matchWholeWord ? IndexOfWholeWord(text, 0, searchText, comparison) : text.IndexOf(searchText, 0, comparison);

                    if (index != -1)
                    {
                        Document.Selection.StartPosition = index;
                        Document.Selection.EndPosition = index + searchText.Length;
                    }
                }
            }

            if (index == -1)
            {
                Document.Selection.StartPosition = Document.Selection.EndPosition;
                return false;
            }

            return true;
        }

        //TODO This method I wrote is pathetic, need to find a way to implement it in a better way 
        public void GetCurrentLineColumn(out int lineIndex, out int columnIndex, out int selectedCount)
        {
            if (_documentLinesCache == null)
            {
                Document.GetText(TextGetOptions.None, out var text);
                _documentLinesCache = text.Split("\r");
            }

            var start = Document.Selection.StartPosition;
            var end = Document.Selection.EndPosition;

            lineIndex = 1;
            columnIndex = 1;
            selectedCount = 0;

            var length = 0;
            bool startLocated = false;
            for (int i = 0; i < _documentLinesCache.Length; i++)
            {
                var line = _documentLinesCache[i];

                if (line.Length + length >= start && !startLocated)
                {
                    lineIndex = i + 1;
                    columnIndex = start - length + 1;
                    startLocated = true;
                }

                if (line.Length + length >= end)
                {
                    if (i == lineIndex - 1)
                        selectedCount = end - start;
                    else
                        selectedCount = end - start + (i - lineIndex);
                    return;
                }

                length += line.Length + 1;
            }
        }

        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            var ctrl = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control);
            var alt = Window.Current.CoreWindow.GetKeyState(VirtualKey.Menu);
            var shift = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift);

            if (!ctrl.HasFlag(CoreVirtualKeyStates.Down) && 
                alt.HasFlag(CoreVirtualKeyStates.Down) && 
                !shift.HasFlag(CoreVirtualKeyStates.Down))
            {
                if (e.Key == VirtualKey.Z)
                {
                    TextWrapping = TextWrapping == TextWrapping.Wrap ? TextWrapping.NoWrap : TextWrapping.Wrap;
                    return;
                }
            }

            if (ctrl.HasFlag(CoreVirtualKeyStates.Down) && !alt.HasFlag(CoreVirtualKeyStates.Down))
            {
                // Disable RichEditBox default shortcuts (Bold, Underline, Italic)
                // https://docs.microsoft.com/en-us/windows/desktop/controls/about-rich-edit-controls
                if (e.Key == VirtualKey.B || e.Key == VirtualKey.I || e.Key == VirtualKey.U ||
                    e.Key == VirtualKey.Number1 || e.Key == VirtualKey.Number2 ||
                    e.Key == VirtualKey.Number3 || e.Key == VirtualKey.Number4 ||
                    e.Key == VirtualKey.Number5 || e.Key == VirtualKey.Number6 || e.Key == VirtualKey.Number7 ||
                    e.Key == VirtualKey.Number8 || e.Key == VirtualKey.Number9)
                {
                    return;
                }

                if (e.Key == VirtualKey.Tab)
                {
                    return; // ignoring tab key here since it is reserved for tab switch
                }

                if (e.Key == VirtualKey.W)
                {
                    if (!shift.HasFlag(CoreVirtualKeyStates.Down))
                    {
                        OnSetClosingKeyDown?.Invoke(this, e);
                    }
                    return;
                }

                if (e.Key == VirtualKey.Z)
                {
                    if (shift.HasFlag(CoreVirtualKeyStates.Down))
                    {
                        Document.Redo();
                    }
                    else
                    {
                        Document.Undo();
                    }
                    return;
                }

                if (e.Key == (VirtualKey)187 || e.Key == VirtualKey.Add) // +
                {
                    SetDefaultTabStop(FontFamily, FontSize + 2);
                    FontSize += 2;
                    return;
                }

                if (e.Key == (VirtualKey)189 || e.Key == VirtualKey.Subtract) // -
                {
                    if (FontSize > 4)
                    {
                        SetDefaultTabStop(FontFamily, FontSize - 2);
                        FontSize -= 2;
                    }
                    return;
                }

                if (e.Key == VirtualKey.Number0 || e.Key == VirtualKey.NumberPad0) // 0
                {
                    SetDefaultTabStop(FontFamily, EditorSettingsService.EditorFontSize);
                    FontSize = EditorSettingsService.EditorFontSize;
                    return;
                }
            }

            base.OnKeyDown(e);
        }

        private string TrimText(string text)
        {
            // Trim end \r
            if (!string.IsNullOrEmpty(text) && text[text.Length - 1] == '\r')
            {
                text = text.Substring(0, text.Length - 1);
            }

            return text;
        }

        private string FixLineEnding(string text, LineEnding lineEnding)
        {
            return LineEndingUtility.ApplyLineEnding(text, lineEnding);
        }

        public void SetText(string text)
        {
            Document.SetText(TextSetOptions.None, text);
        }

        public void ClearUndoQueue()
        {
            // Clear UndoQueue by setting its limit to 0 and set it back
            var undoLimit = Document.UndoLimit;

            // Check to prevent the undo limit stuck on zero
            // because it returns 0 even if the undo limit isn't set yet
            if (undoLimit != 0)
            {
                Document.UndoLimit = 0;
                Document.UndoLimit = undoLimit;
            }
        }
    }
}
