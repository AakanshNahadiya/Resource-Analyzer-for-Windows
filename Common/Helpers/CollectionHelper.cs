using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;

namespace ResourceAnalyzer.Helpers
{
    public static class CollectionHelper
    {
        /// <summary>
        /// Synchronizes an ObservableCollection and associated lookup Dictionary with fresh items in-place.
        /// Preserves existing visual containers, avoids UI thrashing, and retains screen reader focus/selection.
        /// </summary>
        public static void SynchronizeInPlace<TItem, TKey>(
            ObservableCollection<TItem> collection,
            Dictionary<TKey, TItem> map,
            IEnumerable<TItem> newItems,
            Func<TItem, TKey> keySelector,
            Action<TItem, TItem> updateAction,
            ListBox? listBox = null,
            bool isFullReset = false,
            IEqualityComparer<TKey>? keyComparer = null) where TKey : notnull
        {
            TItem? currentSelected = listBox?.SelectedItem is TItem sel ? sel : default;
            TKey? selectedKey = currentSelected != null ? keySelector(currentSelected) : default;

            if (collection.Count == 0 || isFullReset)
            {
                collection.Clear();
                map.Clear();
                foreach (var item in newItems)
                {
                    collection.Add(item);
                    map[keySelector(item)] = item;
                }
                if (listBox != null && collection.Count > 0 && listBox.SelectedIndex < 0)
                {
                    listBox.SelectedIndex = 0;
                }
                return;
            }

            var newKeySet = new HashSet<TKey>(newItems.Select(keySelector), keyComparer ?? EqualityComparer<TKey>.Default);

            // Remove items no longer present
            for (int i = collection.Count - 1; i >= 0; i--)
            {
                var key = keySelector(collection[i]);
                if (!newKeySet.Contains(key))
                {
                    map.Remove(key);
                    collection.RemoveAt(i);
                }
            }

            // Update existing in-place, collect new ones to add
            var toAdd = new List<TItem>();
            foreach (var incoming in newItems)
            {
                var key = keySelector(incoming);
                if (map.TryGetValue(key, out var existing))
                {
                    updateAction(existing, incoming);
                }
                else
                {
                    toAdd.Add(incoming);
                }
            }

            // Append new items
            foreach (var item in toAdd)
            {
                collection.Add(item);
                map[keySelector(item)] = item;
            }

            // Restore selection
            if (listBox != null && selectedKey != null && map.TryGetValue(selectedKey, out var matched))
            {
                listBox.SelectedItem = matched;
            }
            else if (listBox != null && collection.Count > 0 && listBox.SelectedIndex < 0)
            {
                listBox.SelectedIndex = 0;
            }
        }
    }
}
