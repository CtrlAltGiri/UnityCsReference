// Port of the UN3-5 typed style dirtiness traversal plus the UN3-8 kill switch and verify mode
// (Modules/UIElements/Core/VisualTreeStyleUpdater.cs, StyleSheets/StyleInvalidationSettings.cs) onto the
// 6000.3 UI Toolkit internals. 6000.3 has no ids for classes/names and no StyleFeature notifications, so
// features are keyed by strings ("#name", ".class", "Type") and the updater diffs an element's name and
// classes to recover the changed features. Everything else follows the 7000 source line by line.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.StyleSheets;

namespace UN3_8Harness
{
    [Flags]
    internal enum StyleDirtyFlags { None = 0, Self = 1 << 0, Subtree = 1 << 1, HasPendingSet = 1 << 2 }

    internal static class StyleInvalidationSettings
    {
        private static bool s_UseInvalidationSetsValue = true;
        private static bool s_VerifyInvalidationValue;

        internal static bool s_UseInvalidationSets
        {
            get => s_UseInvalidationSetsValue;
            set
            {
                if (s_UseInvalidationSetsValue == value)
                    return;
                s_UseInvalidationSetsValue = value;
                ForceFullRestyle();
            }
        }

        internal static bool s_VerifyInvalidation
        {
            get => s_VerifyInvalidationValue;
            set
            {
                if (s_VerifyInvalidationValue == value)
                    return;
                s_VerifyInvalidationValue = value;
                if (value)
                    ForceFullRestyle();
            }
        }

        internal static int verificationFailureCount { get; set; }

        private static void ForceFullRestyle()
        {
            var iterator = UIElementsUtility.GetPanelsIterator();
            while (iterator.MoveNext())
                iterator.Current.Value.visualTree.IncrementVersion(VersionChangeType.StyleSheet);
        }
    }

    // Harness-only fault injection: pretend the index has no descendant entry for this feature.
    internal static class HarnessFaults
    {
        internal static string s_DropDescendantInvalidationFor;
    }

    internal sealed class PendingInvalidationSet
    {
        private readonly HashSet<string> m_Ids = new HashSet<string>();
        public bool wildcard { get; private set; }

        public void Add(bool addWildcard, IReadOnlyCollection<string> ids)
        {
            wildcard |= addWildcard;
            if (wildcard || ids == null)
                return;
            foreach (var id in ids)
                m_Ids.Add(id);
        }

        public void UnionWith(PendingInvalidationSet other)
        {
            wildcard |= other.wildcard;
            if (wildcard)
                m_Ids.Clear();
            else
                m_Ids.UnionWith(other.m_Ids);
        }

        public bool Matches(VisualElement element)
        {
            if (wildcard)
                return true;
            if (m_Ids.Count == 0)
                return false;
            if ((!string.IsNullOrEmpty(element.name) && m_Ids.Contains("#" + element.name)) || m_Ids.Contains(element.typeName))
                return true;
            foreach (var c in element.GetClassesForIteration())
                if (m_Ids.Contains("." + c))
                    return true;
            return false;
        }

        public void Reset()
        {
            m_Ids.Clear();
            wildcard = false;
        }
    }

    // Port of StyleInvalidationData + PanelStyleInvalidationIndex (rebuilt from the panel's sheets on Refresh).
    internal sealed class HarnessInvalidationIndex
    {
        private readonly HashSet<string> m_Self = new HashSet<string>();
        private readonly Dictionary<string, (bool wildcard, HashSet<string> ids)> m_Descendants = new Dictionary<string, (bool, HashSet<string>)>();
        private bool m_InvalidateSubtree;
        public bool isDirty { get; private set; } = true;
        public bool requiresSubtreeInvalidation => isDirty || m_InvalidateSubtree;

        public void MarkDirty() => isDirty = true;
        public bool ContainsSelf(string id) => m_Self.Contains(id);

        public bool TryGetDescendantInvalidation(string id, out bool wildcard, out IReadOnlyCollection<string> subjectIds)
        {
            if (id != HarnessFaults.s_DropDescendantInvalidationFor && m_Descendants.TryGetValue(id, out var entry))
            {
                wildcard = entry.wildcard;
                subjectIds = entry.ids;
                return true;
            }
            wildcard = false;
            subjectIds = null;
            return false;
        }

        public void Refresh(VisualElement root)
        {
            if (!isDirty)
                return;
            m_Self.Clear();
            m_Descendants.Clear();
            m_InvalidateSubtree = false;
            var sheets = new HashSet<StyleSheet>();
            Collect(root, sheets);
            foreach (var sheet in sheets)
            {
                if (sheet.rules == null)
                    continue;
                foreach (var rule in sheet.rules)
                {
                    if (rule?.complexSelectors == null)
                        continue;
                    foreach (var cs in rule.complexSelectors)
                        AddComplexSelector(cs);
                }
            }
            isDirty = false;
        }

        static void Collect(VisualElement e, HashSet<StyleSheet> sheets)
        {
            if (e.styleSheetList != null)
            {
                foreach (var s in e.styleSheetList)
                {
                    if (s == null) continue;
                    sheets.Add(s);
                    if (s.flattenedRecursiveImports != null)
                        foreach (var i in s.flattenedRecursiveImports)
                            sheets.Add(i);
                }
            }
            for (int i = 0; i < e.hierarchy.childCount; i++)
                Collect(e.hierarchy[i], sheets);
        }

        static string Key(StyleSelectorPart part)
        {
            switch (part.type)
            {
                case StyleSelectorType.ID: return "#" + part.value;
                case StyleSelectorType.Class: return "." + part.value;
                case StyleSelectorType.Type: return part.value;
                default: return null;
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
                    m_InvalidateSubtree = true;
                    return;
                }
            }

            var subject = selectors[selectors.Length - 1];
            foreach (var part in subject.parts)
            {
                var key = Key(part);
                if (key != null)
                    m_Self.Add(key);
            }
            if (selectors.Length == 1)
                return;

            string subjectKey = null;
            int best = 0;
            foreach (var part in subject.parts)
            {
                int rank = part.type == StyleSelectorType.ID ? 3 : part.type == StyleSelectorType.Class ? 2 : part.type == StyleSelectorType.Type ? 1 : 0;
                if (rank > best) { best = rank; subjectKey = Key(part); }
            }

            for (var i = 0; i < selectors.Length - 1; i++)
            {
                foreach (var part in selectors[i].parts)
                {
                    var key = Key(part);
                    if (key == null)
                        continue;
                    if (!m_Descendants.TryGetValue(key, out var set))
                        set = (false, new HashSet<string>());
                    if (subjectKey == null)
                        set.wildcard = true;
                    else
                        set.ids.Add(subjectKey);
                    m_Descendants[key] = set;
                }
            }
        }

        static bool IsAnalyzable(StyleSelector selector)
        {
            if (selector.parts == null || selector.pseudoStateMask == StyleSelector.InvalidPseudoStateMask ||
                selector.negatedPseudoStateMask == StyleSelector.InvalidPseudoStateMask)
                return false;
            foreach (var part in selector.parts)
            {
                switch (part.type)
                {
                    case StyleSelectorType.Wildcard:
                    case StyleSelectorType.PseudoClass:
                    case StyleSelectorType.ID:
                    case StyleSelectorType.Class:
                    case StyleSelectorType.Type:
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }
    }

    // Port of VisualTreeStyleUpdater (7000): installed on a 6000.3 panel with SetUpdater(Styles).
    internal sealed class PortedStyleUpdater : BaseVisualTreeUpdater
    {
        private sealed class FeatureSnapshot
        {
            public string name;
            public HashSet<string> classes = new HashSet<string>();
        }

        private static readonly ProfilerMarker s_ProfilerMarker = new ProfilerMarker("UN3_8.PortedUpdateStyle");
        public override ProfilerMarker profilerMarker => s_ProfilerMarker;

        private uint m_Version;
        private uint m_LastVersion;
        private readonly ConditionalWeakTable<VisualElement, FeatureSnapshot> m_Snapshots = new ConditionalWeakTable<VisualElement, FeatureSnapshot>();
        public readonly HarnessInvalidationIndex index = new HarnessInvalidationIndex();
        public readonly PortedTraversal traversal;

        public PortedStyleUpdater()
        {
            traversal = new PortedTraversal(this);
        }

        internal void RecordSnapshot(VisualElement e)
        {
            var s = m_Snapshots.GetOrCreateValue(e);
            s.name = e.name;
            s.classes.Clear();
            foreach (var c in e.GetClassesForIteration())
                s.classes.Add(c);
        }

        // Stand-in for 7000's IncrementStyleFeatureVersion: recover the changed name/class features.
        private bool TryGetChangedFeatures(VisualElement ve, List<string> changed)
        {
            if (!m_Snapshots.TryGetValue(ve, out var s))
                return false;
            if (s.name != ve.name)
            {
                if (!string.IsNullOrEmpty(s.name)) changed.Add("#" + s.name);
                if (!string.IsNullOrEmpty(ve.name)) changed.Add("#" + ve.name);
            }
            var now = new HashSet<string>(ve.GetClassesForIteration());
            foreach (var c in now) if (!s.classes.Contains(c)) changed.Add("." + c);
            foreach (var c in s.classes) if (!now.Contains(c)) changed.Add("." + c);
            RecordSnapshot(ve);
            return changed.Count > 0;
        }

        public override void OnVersionChanged(VisualElement ve, VersionChangeType versionChangeType)
        {
            if ((versionChangeType & VersionChangeType.StyleSheet) == 0)
                return;
            ++m_Version;
            var changed = new List<string>();
            if (TryGetChangedFeatures(ve, changed))
            {
                traversal.AddChangedElement(ve, changed);
            }
            else
            {
                index.MarkDirty();
                traversal.AddChangedElement(ve);
            }
        }

        public override void Update()
        {
            if (m_Version == m_LastVersion)
                return;
            m_LastVersion = m_Version;
            if (StyleInvalidationSettings.s_UseInvalidationSets)
                index.Refresh(visualTree);
            traversal.PrepareTraversal(panel.scaledPixelsPerPoint);
            traversal.Traverse(visualTree);
        }
    }

    internal sealed class PortedTraversal : HierarchyTraversal
    {
        private struct QueuedStyleChange
        {
            public VisualElement element;
            public StyleDirtyFlags flags;
            public PendingInvalidationSet pendingSet;
        }

        private readonly PortedStyleUpdater m_Updater;
        private readonly StyleVariableContext m_ProcessVarContext = new StyleVariableContext();
        private readonly List<SelectorMatchRecord> m_TempMatchResults = new List<SelectorMatchRecord>();
        private readonly StylePropertyReader m_StylePropertyReader = new StylePropertyReader();
        private readonly List<QueuedStyleChange> m_ApplyStyleUpdateList = new List<QueuedStyleChange>();
        private readonly Dictionary<VisualElement, PendingInvalidationSet> m_PendingSets = new Dictionary<VisualElement, PendingInvalidationSet>();
        private readonly Stack<PendingInvalidationSet> m_PendingSetPool = new Stack<PendingInvalidationSet>();
        private readonly List<PendingInvalidationSet> m_ActivePendingSets = new List<PendingInvalidationSet>();
        // 6000.3 VisualElement has no StyleDirtyFlags bits: keep them on the side.
        private readonly Dictionary<VisualElement, StyleDirtyFlags> m_DirtyFlags = new Dictionary<VisualElement, StyleDirtyFlags>();
        private readonly HashSet<VisualElement> m_AncestorOfDirty = new HashSet<VisualElement>();
        private StyleMatchingContext m_StyleMatchingContext = new StyleMatchingContext(OnProcessMatchResult);
        private bool m_IsApplyingStyles;
        private float currentPixelsPerPoint = 1f;

        // Instrumentation for the harness.
        public readonly List<VisualElement> rematchedLastPass = new List<VisualElement>();
        public int passCount;

        public PortedTraversal(PortedStyleUpdater updater) { m_Updater = updater; }

        public void PrepareTraversal(float pixelsPerPoint) => currentPixelsPerPoint = pixelsPerPoint;

        private static void OnProcessMatchResult(VisualElement current, MatchResultInfo info)
        {
            current.triggerPseudoMask |= info.triggerPseudoMask;
            current.dependencyPseudoMask |= info.dependencyPseudoMask;
        }

        private static void NoOpProcessResult(VisualElement current, MatchResultInfo info) { }

        private StyleDirtyFlags GetFlags(VisualElement e) => m_DirtyFlags.TryGetValue(e, out var f) ? f : StyleDirtyFlags.None;

        private void SetFlags(VisualElement e, StyleDirtyFlags f)
        {
            if (f == StyleDirtyFlags.None) m_DirtyFlags.Remove(e);
            else m_DirtyFlags[e] = f;
        }

        public override void Traverse(VisualElement element)
        {
            rematchedLastPass.Clear();
            passCount++;
            m_IsApplyingStyles = true;
            try
            {
                base.Traverse(element);

                if (StyleInvalidationSettings.s_VerifyInvalidation && StyleInvalidationSettings.s_UseInvalidationSets)
                    VerifyInvalidation(element);
            }
            finally
            {
                m_StyleMatchingContext = new StyleMatchingContext(OnProcessMatchResult);
                m_TempMatchResults.Clear();
                m_ActivePendingSets.Clear();
                foreach (var set in m_PendingSets.Values)
                    ReturnPendingSet(set);
                m_PendingSets.Clear();
                m_IsApplyingStyles = false;
                ProcessQueuedElements();
            }
        }

        public void AddChangedElement(VisualElement ve) => AddChangedElement(ve, StyleDirtyFlags.Subtree, null);

        public void AddChangedElement(VisualElement ve, List<string> changedIds)
        {
            var index = m_Updater.index;
            if (!StyleInvalidationSettings.s_UseInvalidationSets || index.requiresSubtreeInvalidation)
            {
                AddChangedElement(ve);
                return;
            }

            var flags = StyleDirtyFlags.None;
            PendingInvalidationSet pendingSet = null;
            foreach (var id in changedIds)
            {
                if (index.ContainsSelf(id))
                    flags |= StyleDirtyFlags.Self;
                if (index.TryGetDescendantInvalidation(id, out var wildcard, out var subjectIds))
                {
                    pendingSet ??= RentPendingSet();
                    pendingSet.Add(wildcard, subjectIds);
                    flags |= StyleDirtyFlags.HasPendingSet;
                }
            }
            if (flags != StyleDirtyFlags.None)
                AddChangedElement(ve, flags, pendingSet);
        }

        private void AddChangedElement(VisualElement ve, StyleDirtyFlags flags, PendingInvalidationSet pendingSet)
        {
            if (m_IsApplyingStyles)
                m_ApplyStyleUpdateList.Add(new QueuedStyleChange { element = ve, flags = flags, pendingSet = pendingSet });
            else
                MarkStylesDirty(ve, flags, pendingSet);
        }

        private void ProcessQueuedElements()
        {
            foreach (var change in m_ApplyStyleUpdateList)
                MarkStylesDirty(change.element, change.flags, change.pendingSet);
            m_ApplyStyleUpdateList.Clear();
        }

        private void MarkStylesDirty(VisualElement ve, StyleDirtyFlags flags, PendingInvalidationSet pendingSet)
        {
            var current = GetFlags(ve);
            if ((current & StyleDirtyFlags.Subtree) != 0)
            {
                ReturnPendingSet(pendingSet);
            }
            else if ((flags & StyleDirtyFlags.Subtree) != 0)
            {
                if (m_PendingSets.Remove(ve, out var previous))
                    ReturnPendingSet(previous);
                ReturnPendingSet(pendingSet);
                SetFlags(ve, StyleDirtyFlags.Subtree);
            }
            else
            {
                if (pendingSet != null)
                {
                    if (m_PendingSets.TryGetValue(ve, out var existing))
                    {
                        existing.UnionWith(pendingSet);
                        ReturnPendingSet(pendingSet);
                    }
                    else
                    {
                        m_PendingSets.Add(ve, pendingSet);
                    }
                }
                SetFlags(ve, current | flags);
            }

            var parent = ve.hierarchy.parent;
            while (parent != null && m_AncestorOfDirty.Add(parent))
                parent = parent.hierarchy.parent;
        }

        private PendingInvalidationSet RentPendingSet() => m_PendingSetPool.Count > 0 ? m_PendingSetPool.Pop() : new PendingInvalidationSet();

        private void ReturnPendingSet(PendingInvalidationSet pendingSet)
        {
            if (pendingSet == null)
                return;
            pendingSet.Reset();
            m_PendingSetPool.Push(pendingSet);
        }

        private bool NeedsStyleTraversal(VisualElement element)
        {
            if (m_IsVerifyingInvalidation || m_AncestorOfDirty.Contains(element) || GetFlags(element) != StyleDirtyFlags.None || m_ActivePendingSets.Count > 0)
                return true;
            var parent = element.hierarchy.parent;
            return parent != null && (GetFlags(parent) & StyleDirtyFlags.Subtree) != 0;
        }

        private bool MatchesActivePendingSets(VisualElement element)
        {
            foreach (var set in m_ActivePendingSets)
                if (set.Matches(element))
                    return true;
            return false;
        }

        private int AddElementSheets(VisualElement element)
        {
            int originalStyleSheetCount = m_StyleMatchingContext.styleSheetCount;
            if (element.styleSheetList != null)
            {
                foreach (var sheet in element.styleSheetList)
                {
                    if (sheet.flattenedRecursiveImports != null)
                        foreach (var import in sheet.flattenedRecursiveImports)
                            m_StyleMatchingContext.AddStyleSheet(import);
                    m_StyleMatchingContext.AddStyleSheet(sheet);
                }
            }
            return originalStyleSheetCount;
        }

        public override void TraverseRecursive(VisualElement element, int depth)
        {
            if (m_IsVerifyingInvalidation)
            {
                VerifyRecursive(element, depth);
                return;
            }

            if (!NeedsStyleTraversal(element))
                return;

            var parent = element.hierarchy.parent;
            var dirtyFlags = GetFlags(element);
            PendingInvalidationSet pendingSet = null;
            if ((dirtyFlags & StyleDirtyFlags.HasPendingSet) != 0 && !m_PendingSets.Remove(element, out pendingSet))
                dirtyFlags |= StyleDirtyFlags.Subtree;

            bool isSubtreeDirty = (dirtyFlags & StyleDirtyFlags.Subtree) != 0 ||
                (parent != null && (GetFlags(parent) & StyleDirtyFlags.Subtree) != 0);
            bool isDirty = isSubtreeDirty || (dirtyFlags & StyleDirtyFlags.Self) != 0 || MatchesActivePendingSets(element);

            PseudoStates previousTriggerPseudoMask = PseudoStates.None;
            PseudoStates previousDependencyPseudoMask = PseudoStates.None;
            if (isSubtreeDirty)
            {
                SetFlags(element, StyleDirtyFlags.Subtree);
                element.triggerPseudoMask = PseudoStates.None;
                element.dependencyPseudoMask = PseudoStates.None;
            }
            else if (isDirty)
            {
                previousTriggerPseudoMask = element.triggerPseudoMask;
                previousDependencyPseudoMask = element.dependencyPseudoMask;
            }
            int previousInheritedStylesHash = element.inheritedStylesHash;
            int previousVariableHash = isDirty && !isSubtreeDirty ? element.variableContext.GetVariableHash() : 0;

            int originalStyleSheetCount = AddElementSheets(element);
            var originalVariableContext = m_StyleMatchingContext.variableContext;
            if (isDirty)
            {
                rematchedLastPass.Add(element);
                m_Updater.RecordSnapshot(element);
                m_StyleMatchingContext.currentElement = element;
                StyleSelectorHelper.FindMatches(m_StyleMatchingContext, m_TempMatchResults, originalStyleSheetCount - 1);
                if (!isSubtreeDirty)
                {
                    element.triggerPseudoMask |= previousTriggerPseudoMask;
                    element.dependencyPseudoMask |= previousDependencyPseudoMask;
                }
                var newStyle = ProcessMatchedRules(element, m_TempMatchResults);
                newStyle.Acquire();
                if (element.hasInlineStyle)
                    element.inlineStyleAccess.ApplyInlineStyles(ref newStyle);
                element.SetComputedStyle(ref newStyle);
                newStyle.Release();
                element.styleInitialized = true;
                element.inheritedStylesHash = element.computedStyle.inheritedData.GetHashCode();
                m_StyleMatchingContext.currentElement = null;
                m_TempMatchResults.Clear();

                if (!isSubtreeDirty && (element.inheritedStylesHash != previousInheritedStylesHash ||
                                        element.variableContext.GetVariableHash() != previousVariableHash))
                {
                    isSubtreeDirty = true;
                    SetFlags(element, StyleDirtyFlags.Subtree);
                }
            }
            else
            {
                m_StyleMatchingContext.variableContext = element.variableContext;
            }

            bool pushPendingSet = pendingSet != null && !isSubtreeDirty;
            if (pushPendingSet)
                m_ActivePendingSets.Add(pendingSet);
            m_StyleMatchingContext.ancestorFilter.PushElement(element);

            Recurse(element, depth);

            m_StyleMatchingContext.ancestorFilter.PopElement();
            if (pushPendingSet)
                m_ActivePendingSets.RemoveAt(m_ActivePendingSets.Count - 1);
            ReturnPendingSet(pendingSet);

            SetFlags(element, StyleDirtyFlags.None);
            m_AncestorOfDirty.Remove(element);

            m_StyleMatchingContext.variableContext = originalVariableContext;
            if (m_StyleMatchingContext.styleSheetCount > originalStyleSheetCount)
                m_StyleMatchingContext.RemoveStyleSheetRange(originalStyleSheetCount, m_StyleMatchingContext.styleSheetCount - originalStyleSheetCount);
        }

        private ComputedStyle ProcessMatchedRules(VisualElement element, List<SelectorMatchRecord> matchingSelectors)
        {
            matchingSelectors.Sort((a, b) => SelectorMatchRecord.Compare(a, b));

            long matchingRulesHash = ComputeMatchingRulesHash(element, matchingSelectors, out var variableContext);
            m_StyleMatchingContext.variableContext = variableContext;
            element.variableContext = variableContext;
            if (StyleInvalidationSettings.s_VerifyInvalidation)
                RecordMatch(element, matchingRulesHash);

            var parent = element.hierarchy.parent;
            if (!StyleCache.TryGetValue(matchingRulesHash, out ComputedStyle resolvedStyles))
            {
                ref var parentStyle = ref parent != null ? ref parent.computedStyle : ref InitialStyle.Get();
                resolvedStyles = ComputedStyle.Create(ref parentStyle);
                resolvedStyles.matchingRulesHash = matchingRulesHash;
                float dpiScaling = element.scaledPixelsPerPoint;
                foreach (var record in matchingSelectors)
                {
                    m_StylePropertyReader.SetContext(record.sheet, record.complexSelector, m_StyleMatchingContext.variableContext, dpiScaling);
                    resolvedStyles.ApplyProperties(m_StylePropertyReader, ref parentStyle);
                }
                resolvedStyles.FinalizeApply(ref parentStyle);
                StyleCache.SetValue(matchingRulesHash, ref resolvedStyles);
            }
            return resolvedStyles;
        }

        private long ComputeMatchingRulesHash(VisualElement element, List<SelectorMatchRecord> matches, out StyleVariableContext variableContext)
        {
            long matchingRulesHash = element.fullTypeName.GetHashCode();
            matchingRulesHash = (matchingRulesHash * 397) ^ currentPixelsPerPoint.GetHashCode();

            variableContext = m_StyleMatchingContext.variableContext;
            int oldVariablesHash = variableContext.GetVariableHash();
            int customPropertiesCount = 0;
            foreach (var m in matches)
                customPropertiesCount += m.complexSelector.rule.customPropertiesCount;
            if (customPropertiesCount > 0)
                m_ProcessVarContext.AddInitialRange(variableContext);

            foreach (var match in matches)
            {
                matchingRulesHash = (matchingRulesHash * 397) ^ match.sheet.contentHash;
                matchingRulesHash = (matchingRulesHash * 397) ^ match.complexSelector.ruleIndex;
                matchingRulesHash = (matchingRulesHash * 397) ^ match.complexSelector.specificity;
                var rule = match.complexSelector.rule;
                if (rule.customPropertiesCount > 0)
                {
                    foreach (var property in rule.properties)
                        if (property.isCustomProperty)
                            m_ProcessVarContext.Add(new StyleVariable(property.name, match.sheet, property.values));
                }
            }

            var parent = element.hierarchy.parent;
            int inheritedStyleHash = parent?.inheritedStylesHash ?? 0;
            matchingRulesHash = (matchingRulesHash * 397) ^ inheritedStyleHash;

            int variablesHash = oldVariablesHash;
            if (customPropertiesCount > 0)
                variablesHash = m_ProcessVarContext.GetVariableHash();
            matchingRulesHash = (matchingRulesHash * 397) ^ variablesHash;

            if (oldVariablesHash != variablesHash)
            {
                if (!StyleCache.TryGetValue(variablesHash, out variableContext))
                {
                    variableContext = new StyleVariableContext(m_ProcessVarContext);
                    StyleCache.SetValue(variablesHash, variableContext);
                }
            }
            m_ProcessVarContext.Clear();
            return matchingRulesHash;
        }

        // ---- s_VerifyInvalidation (same code as the 7000 change) ----
        private sealed class RecordedMatch { public long matchingRulesHash; }

        private const int k_MaxLoggedVerificationFailures = 10;
        private ConditionalWeakTable<VisualElement, RecordedMatch> m_RecordedMatches;
        private StyleMatchingContext m_VerifyMatchingContext;
        private HashSet<VisualElement> m_VerifySkippedElements;
        private bool m_IsVerifyingInvalidation;
        private int m_VerifySkipDepth;
        private int m_VerifyFailureCount;
        public int verifiedElementsLastPass;

        private void RecordMatch(VisualElement element, long matchingRulesHash)
        {
            m_RecordedMatches ??= new ConditionalWeakTable<VisualElement, RecordedMatch>();
            m_RecordedMatches.GetOrCreateValue(element).matchingRulesHash = matchingRulesHash;
        }

        private void VerifyInvalidation(VisualElement root)
        {
            if (m_ApplyStyleUpdateList.Count > 0)
            {
                m_VerifySkippedElements ??= new HashSet<VisualElement>();
                foreach (var change in m_ApplyStyleUpdateList)
                    m_VerifySkippedElements.Add(change.element);
            }

            // 6000.3's StyleMatchingContext has no Reset(): use a fresh one per pass.
            m_VerifyMatchingContext = new StyleMatchingContext(NoOpProcessResult);
            var styleMatchingContext = m_StyleMatchingContext;
            m_StyleMatchingContext = m_VerifyMatchingContext;
            m_IsVerifyingInvalidation = true;
            m_VerifyFailureCount = 0;
            verifiedElementsLastPass = 0;
            try
            {
                base.Traverse(root);
            }
            finally
            {
                m_IsVerifyingInvalidation = false;
                m_VerifySkipDepth = 0;
                m_VerifySkippedElements?.Clear();
                m_TempMatchResults.Clear();
                m_StyleMatchingContext = styleMatchingContext;
            }

            if (m_VerifyFailureCount > k_MaxLoggedVerificationFailures)
                Debug.LogError($"Style invalidation verification: {m_VerifyFailureCount - k_MaxLoggedVerificationFailures} more mismatching elements not logged.");
        }

        private void VerifyRecursive(VisualElement element, int depth)
        {
            bool skip = m_VerifySkipDepth > 0 || (m_VerifySkippedElements != null && m_VerifySkippedElements.Contains(element));

            int originalStyleSheetCount = AddElementSheets(element);
            var originalVariableContext = m_StyleMatchingContext.variableContext;
            if (!skip)
            {
                m_StyleMatchingContext.currentElement = element;
                StyleSelectorHelper.FindMatches(m_StyleMatchingContext, m_TempMatchResults, originalStyleSheetCount - 1);
                m_TempMatchResults.Sort((a, b) => SelectorMatchRecord.Compare(a, b));
                var expectedHash = ComputeMatchingRulesHash(element, m_TempMatchResults, out var expectedVariableContext);
                m_StyleMatchingContext.currentElement = null;
                m_TempMatchResults.Clear();
                verifiedElementsLastPass++;
                CheckVerifiedElement(element, expectedHash, expectedVariableContext);
            }
            else
            {
                m_VerifySkipDepth++;
            }

            m_StyleMatchingContext.variableContext = element.variableContext;
            m_StyleMatchingContext.ancestorFilter.PushElement(element);

            Recurse(element, depth);

            m_StyleMatchingContext.ancestorFilter.PopElement();
            if (skip)
                m_VerifySkipDepth--;

            m_StyleMatchingContext.variableContext = originalVariableContext;
            if (m_StyleMatchingContext.styleSheetCount > originalStyleSheetCount)
                m_StyleMatchingContext.RemoveStyleSheetRange(originalStyleSheetCount, m_StyleMatchingContext.styleSheetCount - originalStyleSheetCount);
        }

        private void CheckVerifiedElement(VisualElement element, long expectedHash, StyleVariableContext expectedVariableContext)
        {
            if (m_RecordedMatches == null || !m_RecordedMatches.TryGetValue(element, out var recorded))
                return;

            string reason = null;
            if (recorded.matchingRulesHash != expectedHash)
                reason = $"matchingRulesHash is {recorded.matchingRulesHash} but a full re-match gives {expectedHash}";

            int variableHash = element.variableContext.GetVariableHash();
            int expectedVariableHash = expectedVariableContext.GetVariableHash();
            if (variableHash != expectedVariableHash)
            {
                var variableReason = $"variableContext hash is {variableHash} but a full re-match gives {expectedVariableHash}";
                reason = reason == null ? variableReason : reason + "; " + variableReason;
            }

            if (reason == null)
                return;

            StyleInvalidationSettings.verificationFailureCount++;
            if (++m_VerifyFailureCount <= k_MaxLoggedVerificationFailures)
                Debug.LogError($"Style invalidation verification failed for {GetElementPath(element)}: {reason}. The element was not re-matched after a change that affects it.");
        }

        internal static string GetElementPath(VisualElement element)
        {
            var path = new List<VisualElement>();
            for (var current = element; current != null; current = current.hierarchy.parent)
                path.Add(current);

            var builder = new StringBuilder();
            for (int i = path.Count - 1; i >= 0; i--)
            {
                var current = path[i];
                builder.Append(current.typeName);
                if (!string.IsNullOrEmpty(current.name))
                    builder.Append('#').Append(current.name);
                foreach (var className in current.GetClasses())
                    builder.Append('.').Append(className);
                if (i > 0)
                    builder.Append(" > ");
            }
            return builder.ToString();
        }
    }
}
