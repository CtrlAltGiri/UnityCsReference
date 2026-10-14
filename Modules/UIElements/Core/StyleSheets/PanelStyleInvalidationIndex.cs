// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System.Collections.Generic;

namespace UnityEngine.UIElements
{
    // Conservative union of the StyleInvalidationData of every style sheet attached to an element
    // of a panel. Style sheets are scoped per subtree, so a mutation can't cheaply tell which
    // sheets apply to it; consulting the union only ever over-invalidates.
    //
    // Sheet references are ref-counted (one per element holding the sheet in its styleSheetList)
    // and folded into the union lazily by Refresh. While isDirty is set the union may not match
    // the attached sheets and callers must fall back to full-subtree invalidation.
    internal sealed class PanelStyleInvalidationIndex
    {
        sealed class DescendantEntry
        {
            // Number of contributed sheets that have this key in their descendant map.
            public int refCount;
            public int wildcardCount;
            public readonly Dictionary<int, int> idCounts = new Dictionary<int, int>();
        }

        // Elements of the panel referencing each sheet.
        readonly Dictionary<StyleSheet, int> m_SheetRefCounts = new Dictionary<StyleSheet, int>();

        // Data currently folded into the union, per sheet. Can lag behind m_SheetRefCounts until Refresh.
        readonly Dictionary<StyleSheet, StyleInvalidationData> m_Contributions = new Dictionary<StyleSheet, StyleInvalidationData>();

        // Sheets whose contribution may be out of date.
        readonly HashSet<StyleSheet> m_PendingSheets = new HashSet<StyleSheet>();

        readonly Dictionary<int, int> m_SelfCounts = new Dictionary<int, int>();
        readonly Dictionary<int, DescendantEntry> m_Descendants = new Dictionary<int, DescendantEntry>();
        int m_InvalidateSubtreeCount;

        bool m_RevalidateAll;
        uint m_DataGeneration;

        public PanelStyleInvalidationIndex()
        {
            m_DataGeneration = StyleInvalidationData.generation;
        }

        // Incremented every time the contents of the index may have changed.
        public uint version { get; private set; }

        public bool isDirty => m_PendingSheets.Count > 0 || m_RevalidateAll || m_DataGeneration != StyleInvalidationData.generation;

        // True when some attached sheet has selectors that can't be analysed, or when the index
        // is dirty: either way only a full-subtree invalidation is safe.
        public bool requiresSubtreeInvalidation => isDirty || m_InvalidateSubtreeCount > 0;

        public int sheetCount => m_SheetRefCounts.Count;

        public int GetSheetRefCount(StyleSheet styleSheet)
        {
            return styleSheet is not null && m_SheetRefCounts.TryGetValue(styleSheet, out var count) ? count : 0;
        }

        // Whether a selector of an attached sheet uses this feature (name, class or type id) in its subject.
        public bool ContainsSelf(int uniqueStyleStringId) => m_SelfCounts.ContainsKey(uniqueStyleStringId);

        // Whether a change of this feature on an element may change the matched rules of its descendants.
        public bool HasDescendantInvalidation(int uniqueStyleStringId) => m_Descendants.ContainsKey(uniqueStyleStringId);

        // Descendant subject keys to invalidate when the feature changes. wildcard means every descendant.
        public bool TryGetDescendantInvalidation(int uniqueStyleStringId, out bool wildcard, out IReadOnlyCollection<int> subjectIds)
        {
            if (m_Descendants.TryGetValue(uniqueStyleStringId, out var entry))
            {
                wildcard = entry.wildcardCount > 0;
                subjectIds = entry.idCounts.Keys;
                return true;
            }

            wildcard = false;
            subjectIds = null;
            return false;
        }

        public void AddElement(VisualElement element)
        {
            var sheets = element.styleSheetList;
            if (sheets == null)
                return;

            foreach (var sheet in sheets)
                AddSheet(sheet);
        }

        public void RemoveElement(VisualElement element)
        {
            var sheets = element.styleSheetList;
            if (sheets == null)
                return;

            foreach (var sheet in sheets)
                RemoveSheet(sheet);
        }

        public void AddSheet(StyleSheet styleSheet)
        {
            if (styleSheet is null)
                return;

            m_SheetRefCounts.TryGetValue(styleSheet, out var count);
            m_SheetRefCounts[styleSheet] = count + 1;
            if (count == 0)
            {
                m_PendingSheets.Add(styleSheet);
                ++version;
            }
        }

        public void RemoveSheet(StyleSheet styleSheet)
        {
            if (styleSheet is null || !m_SheetRefCounts.TryGetValue(styleSheet, out var count))
                return;

            if (count > 1)
            {
                m_SheetRefCounts[styleSheet] = count - 1;
                return;
            }

            m_SheetRefCounts.Remove(styleSheet);
            m_PendingSheets.Add(styleSheet);
            ++version;
        }

        // Called on live reload / reimport: sheet contents may have changed, re-validate every contribution.
        public void MarkDirty()
        {
            m_RevalidateAll = true;
            ++version;
        }

        public void Clear()
        {
            m_SheetRefCounts.Clear();
            m_Contributions.Clear();
            m_PendingSheets.Clear();
            m_SelfCounts.Clear();
            m_Descendants.Clear();
            m_InvalidateSubtreeCount = 0;
            m_RevalidateAll = false;
            m_DataGeneration = StyleInvalidationData.generation;
            ++version;
        }

        // Brings the union back in sync with the attached sheets. Building a sheet's data goes
        // through SelectorAccelerationCache, which styling does for every attached sheet anyway.
        public void Refresh()
        {
            if (!isDirty)
                return;

            if (m_RevalidateAll || m_DataGeneration != StyleInvalidationData.generation)
            {
                foreach (var sheet in m_Contributions.Keys)
                    m_PendingSheets.Add(sheet);
                foreach (var sheet in m_SheetRefCounts.Keys)
                    m_PendingSheets.Add(sheet);
            }

            m_RevalidateAll = false;
            m_DataGeneration = StyleInvalidationData.generation;

            bool changed = false;
            List<StyleSheet> destroyedSheets = null;
            foreach (var sheet in m_PendingSheets)
            {
                StyleInvalidationData target = null;
                if (m_SheetRefCounts.ContainsKey(sheet))
                {
                    if (sheet != null)
                        target = GetInvalidationData(sheet);
                    else
                        (destroyedSheets ??= new List<StyleSheet>()).Add(sheet);
                }

                m_Contributions.TryGetValue(sheet, out var current);
                if (ReferenceEquals(current, target))
                    continue;

                if (current != null)
                {
                    Subtract(current);
                    m_Contributions.Remove(sheet);
                }

                if (target != null)
                {
                    Add(target);
                    m_Contributions.Add(sheet, target);
                }

                changed = true;
            }
            m_PendingSheets.Clear();

            // A destroyed sheet styles nothing and can't come back. Its holders may drop it without
            // going through VisualElementStyleSheetSet, so stop tracking it here.
            if (destroyedSheets != null)
            {
                foreach (var sheet in destroyedSheets)
                    m_SheetRefCounts.Remove(sheet);
            }

            // Building the data may rebuild a sheet, which can bump the generation again; the
            // contributions above already reflect the latest data.
            m_DataGeneration = StyleInvalidationData.generation;

            if (changed)
                ++version;
        }

        static StyleInvalidationData GetInvalidationData(StyleSheet styleSheet)
        {
            return SelectorAccelerationCache.shared.GetOrCreate(styleSheet).invalidationData ?? StyleInvalidationData.Build(styleSheet);
        }

        void Add(StyleInvalidationData data)
        {
            if (data.invalidateSubtree)
                ++m_InvalidateSubtreeCount;

            foreach (var id in data.self)
            {
                m_SelfCounts.TryGetValue(id, out var count);
                m_SelfCounts[id] = count + 1;
            }

            foreach (var kv in data.descendant)
            {
                if (!m_Descendants.TryGetValue(kv.Key, out var entry))
                {
                    entry = new DescendantEntry();
                    m_Descendants.Add(kv.Key, entry);
                }

                ++entry.refCount;
                if (kv.Value.wildcard)
                    ++entry.wildcardCount;

                foreach (var id in kv.Value.ids)
                {
                    entry.idCounts.TryGetValue(id, out var count);
                    entry.idCounts[id] = count + 1;
                }
            }
        }

        void Subtract(StyleInvalidationData data)
        {
            if (data.invalidateSubtree)
                --m_InvalidateSubtreeCount;

            foreach (var id in data.self)
                Decrement(m_SelfCounts, id);

            foreach (var kv in data.descendant)
            {
                if (!m_Descendants.TryGetValue(kv.Key, out var entry))
                    continue;

                if (kv.Value.wildcard)
                    --entry.wildcardCount;

                foreach (var id in kv.Value.ids)
                    Decrement(entry.idCounts, id);

                if (--entry.refCount == 0)
                    m_Descendants.Remove(kv.Key);
            }
        }

        static void Decrement(Dictionary<int, int> counts, int key)
        {
            if (!counts.TryGetValue(key, out var count))
                return;

            if (count > 1)
                counts[key] = count - 1;
            else
                counts.Remove(key);
        }

        // Union as a single StyleInvalidationData, to compare against the expected contents.
        internal StyleInvalidationData CreateSnapshot()
        {
            var snapshot = new StyleInvalidationData { invalidateSubtree = m_InvalidateSubtreeCount > 0 };
            foreach (var id in m_SelfCounts.Keys)
                snapshot.self.Add(id);

            foreach (var kv in m_Descendants)
            {
                var set = new InvalidationSet { wildcard = kv.Value.wildcardCount > 0 };
                foreach (var id in kv.Value.idCounts.Keys)
                    set.ids.Add(id);
                snapshot.descendant.Add(kv.Key, set);
            }
            return snapshot;
        }
    }
}
