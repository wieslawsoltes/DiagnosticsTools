using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Avalonia.Diagnostics
{
    internal static class ClipboardUtilities
    {
        public static Task SetTextAsync(IClipboard clipboard, string text, string? selector = null)
        {
            if (clipboard is null)
            {
                throw new ArgumentNullException(nameof(clipboard));
            }

            var data = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [DataFormats.Text] = text
            };

            if (!string.IsNullOrEmpty(selector))
            {
                data[Constants.DataFormats.Avalonia_DevTools_Selector] = selector;
            }

            return clipboard.SetDataObjectAsync(new DictionaryDataObject(data));
        }

        private sealed class DictionaryDataObject : IDataObject
        {
            private readonly IReadOnlyDictionary<string, object?> _data;

            public DictionaryDataObject(IReadOnlyDictionary<string, object?> data)
            {
                _data = data ?? throw new ArgumentNullException(nameof(data));
            }

            public bool Contains(string dataFormat)
            {
                return _data.ContainsKey(dataFormat);
            }

            public object? Get(string dataFormat)
            {
                return _data.TryGetValue(dataFormat, out var value) ? value : null;
            }

            public IEnumerable<string> GetDataFormats()
            {
                return _data.Keys;
            }
        }
    }
}
