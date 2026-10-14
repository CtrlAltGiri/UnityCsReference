// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System.Collections.Generic;

namespace UnityEngine.UIElements
{
    // Elements below a changed ancestor that may need restyling: those whose subject key
    // (name, else class, else type) is in ids, or every descendant when wildcard is set.
    internal sealed class InvalidationSet
    {
        public readonly HashSet<int> ids = new HashSet<int>();
        public bool wildcard;
    }

    // Per-sheet (imports included) record of which name/class/type features, keyed by
    // UniqueStyleString.id, can affect the element itself (self) or its descendants (descendant).
    // Built alongside the SelectorAccelerationCacheEntry and shares its lifetime.
    internal sealed class StyleInvalidationData
    {
        public readonly HashSet<int> self = new HashSet<int>();
        public readonly Dictionary<int, InvalidationSet> descendant = new Dictionary<int, InvalidationSet>();

        // Set when a selector can't be analysed (predicates, recursive pseudo-classes, unknown
        // pseudo-states): any change must fall back to invalidating the whole subtree.
        public bool invalidateSubtree;

        // Bumped whenever cached data may have been discarded or rebuilt (style sheet change,
        // reimport, unload), so consumers holding data instances know to re-validate them.
        internal static uint generation { get; private set; }

        internal static void IncrementGeneration() => ++generation;

        internal static StyleInvalidationData Build(StyleSheet styleSheet)
        {
            var data = new StyleInvalidationData();
            if (styleSheet == null)
                return data;

            data.AddStyleSheet(styleSheet);
            var imports = styleSheet.flattenedRecursiveImports;
            if (imports != null)
            {
                foreach (var sheet in imports)
                {
                    if (sheet != null)
                        data.AddStyleSheet(sheet);
                }
            }
            return data;
        }

        void AddStyleSheet(StyleSheet styleSheet)
        {
            var rules = styleSheet.rules;
            if (rules == null)
                return;

            foreach (var rule in rules)
            {
                if (rule?.complexSelectors == null)
                    continue;

                foreach (var complexSelector in rule.complexSelectors)
                    AddComplexSelector(complexSelector);
            }
        }

        void AddComplexSelector(StyleComplexSelector complexSelector)
        {
            var selectors = complexSelector?.selectors;
            if (selectors == null || selectors.Length == 0)
                return;

            foreach (var selector in selectors)
            {
                if (!IsAnalyzable(selector))
                {
                    invalidateSubtree = true;
                    return;
                }
            }

            var subject = selectors[selectors.Length - 1];
            foreach (var part in subject.parts)
            {
                if (IsFeature(part.type))
                    self.Add(part.cachedUniqueStyleStringId);
            }

            if (selectors.Length == 1)
                return;

            bool subjectIsWildcard = !TryGetSubjectKey(subject, out var subjectKey);
            for (var i = 0; i < selectors.Length - 1; i++)
            {
                foreach (var part in selectors[i].parts)
                {
                    if (!IsFeature(part.type))
                        continue;

                    if (!descendant.TryGetValue(part.cachedUniqueStyleStringId, out var set))
                    {
                        set = new InvalidationSet();
                        descendant.Add(part.cachedUniqueStyleStringId, set);
                    }

                    if (subjectIsWildcard)
                        set.wildcard = true;
                    else
                        set.ids.Add(subjectKey);
                }
            }
        }

        static bool IsFeature(StyleSelectorType type)
        {
            return type == StyleSelectorType.ID || type == StyleSelectorType.Class || type == StyleSelectorType.Type;
        }

        static bool IsAnalyzable(StyleSelector selector)
        {
            if (selector.parts == null ||
                selector.pseudoStateMask == StyleSelector.InvalidPseudoStateMask ||
                selector.negatedPseudoStateMask == StyleSelector.InvalidPseudoStateMask)
                return false;

            foreach (var part in selector.parts)
            {
                switch (part.type)
                {
                    case StyleSelectorType.Wildcard:
                    case StyleSelectorType.PseudoClass:
                        break;
                    case StyleSelectorType.ID:
                    case StyleSelectorType.Class:
                    case StyleSelectorType.Type:
                        if (part.cachedUniqueStyleStringId < 0)
                            return false;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        // Most selective key of the rightmost compound: name > class > type.
        static bool TryGetSubjectKey(StyleSelector subject, out int key)
        {
            key = -1;
            var best = StyleSelectorType.Unknown;
            foreach (var part in subject.parts)
            {
                if (Rank(part.type) > Rank(best))
                {
                    best = part.type;
                    key = part.cachedUniqueStyleStringId;
                }
            }
            return key >= 0;
        }

        static int Rank(StyleSelectorType type)
        {
            switch (type)
            {
                case StyleSelectorType.ID: return 3;
                case StyleSelectorType.Class: return 2;
                case StyleSelectorType.Type: return 1;
                default: return 0;
            }
        }
    }
}
