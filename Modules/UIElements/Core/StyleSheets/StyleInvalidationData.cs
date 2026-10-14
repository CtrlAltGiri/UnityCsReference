// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System.Collections.Generic;

namespace UnityEngine.UIElements
{
    // Describes which elements under an element may need restyling when a feature (class, name
    // or type) toggles on that element. Subject keys are UniqueStyleString ids of the rightmost
    // compound of the selectors that reference the feature in an ancestor position.
    internal sealed class InvalidationSet
    {
        public readonly HashSet<int> names = new();
        public readonly HashSet<int> classes = new();
        public readonly HashSet<int> types = new();

        // Some selector has a subject with no name/class/type (e.g. `.a *`, `.a > :hover`):
        // every element below may match.
        public bool wildcard;

        // Some selector can't be analyzed precisely (predicates, recursive pseudo-classes,
        // unknown pseudo-classes): restyle the whole subtree.
        public bool invalidateSubtree;

        // True when every contributing selector reaches its subject through a direct-child
        // relationship (`.a > .b`), so only immediate children need to be visited.
        public bool childOnly;

        bool m_HasContribution;

        internal void AddRelationship(bool isChild)
        {
            childOnly = m_HasContribution ? childOnly && isChild : isChild;
            m_HasContribution = true;
        }

        internal void AddSubject(StyleSelector subject)
        {
            if (!TryGetSubjectKey(subject, out var type, out var id))
            {
                wildcard = true;
                return;
            }

            switch (type)
            {
                case StyleSelectorType.ID:
                    names.Add(id);
                    break;
                case StyleSelectorType.Class:
                    classes.Add(id);
                    break;
                default:
                    types.Add(id);
                    break;
            }
        }

        // Picks the most selective feature of a compound: name > class > type. Returns false
        // when the compound has none of them (wildcard).
        internal static bool TryGetSubjectKey(StyleSelector selector, out StyleSelectorType type, out int id)
        {
            type = StyleSelectorType.Unknown;
            id = -1;
            foreach (var part in selector.parts)
            {
                if (part.type == StyleSelectorType.ID)
                {
                    type = part.type;
                    id = part.cachedUniqueStyleStringId;
                    return true;
                }

                if (part.type == StyleSelectorType.Class && type != StyleSelectorType.Class)
                {
                    type = part.type;
                    id = part.cachedUniqueStyleStringId;
                }
                else if (part.type == StyleSelectorType.Type && type == StyleSelectorType.Unknown)
                {
                    type = part.type;
                    id = part.cachedUniqueStyleStringId;
                }
            }

            return type != StyleSelectorType.Unknown;
        }
    }

    // Per-StyleSheet invalidation metadata, built alongside the selector acceleration cache (and
    // over the same flattened recursive imports). Keys are UniqueStyleString ids of class, name and
    // type features; ids are shared between these kinds, so a lookup may over-invalidate when the
    // same string is used as e.g. both a class and a type, but never under-invalidates.
    //
    // Managed side table on purpose: SelectorAccelerationCacheCore mirrors a C++ header.
    internal sealed class StyleInvalidationData
    {
        // Features that appear in the rightmost compound of some selector: toggling them may
        // change the matched rules of the element itself.
        public readonly HashSet<int> self = new();

        // Features that appear in an ancestor compound of some selector: toggling them may change
        // the matched rules of descendants described by the set.
        public readonly Dictionary<int, InvalidationSet> descendant = new();

        // SelectorAccelerationCacheEntry.m_BuildId of the entry this data was built with.
        internal long buildId;

        public bool AffectsSelf(int featureId) => self.Contains(featureId);

        public bool TryGetDescendantSet(int featureId, out InvalidationSet set) => descendant.TryGetValue(featureId, out set);

        public static StyleInvalidationData Build(StyleSheet styleSheet, long buildId = 0)
        {
            var data = new StyleInvalidationData { buildId = buildId };
            if (styleSheet == null)
                return data;

            data.AddStyleSheet(styleSheet);

            if (styleSheet.flattenedRecursiveImports != null)
            {
                foreach (var sheet in styleSheet.flattenedRecursiveImports)
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

        internal void AddComplexSelector(StyleComplexSelector complexSelector)
        {
            var selectors = complexSelector?.selectors;
            if (selectors == null || selectors.Length == 0)
                return;

            bool requiresSubtree = RequiresSubtreeInvalidation(selectors);

            // Walk right to left: the rightmost compound is the subject.
            int subjectIndex = selectors.Length - 1;
            var subject = selectors[subjectIndex];
            AddFeatures(subject, self);

            for (int i = subjectIndex - 1; i >= 0; i--)
            {
                // Only the compound right before the subject, joined by `>`, is child-only.
                bool isChild = i == subjectIndex - 1 && subject.previousRelationship == StyleSelectorRelationship.Child;

                foreach (var part in selectors[i].parts)
                {
                    if (!IsKeyedFeature(part.type))
                        continue;

                    var set = GetOrCreateDescendantSet(part.cachedUniqueStyleStringId);
                    set.AddRelationship(isChild);
                    if (requiresSubtree)
                        set.invalidateSubtree = true;
                    else
                        set.AddSubject(subject);
                }
            }
        }

        InvalidationSet GetOrCreateDescendantSet(int featureId)
        {
            if (!descendant.TryGetValue(featureId, out var set))
            {
                set = new InvalidationSet();
                descendant.Add(featureId, set);
            }
            return set;
        }

        static void AddFeatures(StyleSelector selector, HashSet<int> target)
        {
            foreach (var part in selector.parts)
            {
                if (IsKeyedFeature(part.type))
                    target.Add(part.cachedUniqueStyleStringId);
            }
        }

        static bool IsKeyedFeature(StyleSelectorType type)
        {
            return type == StyleSelectorType.ID || type == StyleSelectorType.Class || type == StyleSelectorType.Type;
        }

        static bool RequiresSubtreeInvalidation(StyleSelector[] selectors)
        {
            foreach (var selector in selectors)
            {
                if (selector.pseudoStateMask == StyleSelector.InvalidPseudoStateMask)
                    return true;

                foreach (var part in selector.parts)
                {
                    if (part.type == StyleSelectorType.Predicate ||
                        part.type == StyleSelectorType.RecursivePseudoClass ||
                        part.type == StyleSelectorType.Unknown)
                        return true;
                }
            }
            return false;
        }
    }
}
