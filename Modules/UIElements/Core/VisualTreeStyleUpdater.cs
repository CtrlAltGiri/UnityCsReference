// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Unity.Profiling;
using UnityEngine.Bindings;
using UnityEngine.UIElements.StyleSheets;

namespace UnityEngine.UIElements
{
    [VisibleToOtherModules("UnityEditor.UIBuilderModule")]
    internal static partial class StyleCache
    {
        // hash of a set of rules to a specified style data
        // the same set of rules will give the same specified styles, caching the hash of the matching rules before
        // resolving styles allows to skip the resolve part when an existing style data already exists
        // ClearStyleCache (registered on UnloadingUtility below) owns cleanup: it releases the
        // ComputedStyle native refcounts before clearing, which a generated Clear() would leak.
        [NoAutoStaticsCleanup] // cleaned by ClearStyleCache on unloading
        private static Dictionary<Int64, ComputedStyle> s_ComputedStyleCache = new Dictionary<Int64, ComputedStyle>();
        [NoAutoStaticsCleanup] // cleaned by ClearStyleCache on unloading
        private static Dictionary<int, StyleVariableContext> s_StyleVariableContextCache = new Dictionary<int, StyleVariableContext>();

        // Cached values for TransitionData converted into a ComputedTransitionProperty array for easier access
        [NoAutoStaticsCleanup] // cleaned by ClearStyleCache on unloading
        private static Dictionary<int, ComputedTransitionProperty[]> s_ComputedTransitionsCache = new Dictionary<int, ComputedTransitionProperty[]>();

        static StyleCache()
        {
            UnloadingUtility.InitializeOnEveryCodeLoad(UnloadingSubscriber.StyleCache, Reinitialize);
        }

        static void Reinitialize()
        {
            UnloadingUtility.SubscribeToUnloading(UnloadingSubscriber.StyleCache, ClearStyleCache);
        }

        public static bool TryGetValue(Int64 hash, out ComputedStyle data)
        {
            return s_ComputedStyleCache.TryGetValue(hash, out data);
        }

        public static void SetValue(Int64 hash, ref ComputedStyle data)
        {
            // No need to acquire ComputedStyle here because it's freshly created and already has the correct ref count.
            // Release the entry being replaced so the cache's own ref doesn't leak when an existing hash is re-resolved.
            if (s_ComputedStyleCache.TryGetValue(hash, out var existing))
                existing.Release();
            s_ComputedStyleCache[hash] = data;
        }

        public static bool TryGetValue(int hash, out StyleVariableContext data)
        {
            return s_StyleVariableContextCache.TryGetValue(hash, out data);
        }

        public static void SetValue(int hash, StyleVariableContext data)
        {
            s_StyleVariableContextCache[hash] = data;
        }

        public static bool TryGetValue(int hash, out ComputedTransitionProperty[] data)
        {
            return s_ComputedTransitionsCache.TryGetValue(hash, out data);
        }

        public static void SetValue(int hash, ComputedTransitionProperty[] data)
        {
            s_ComputedTransitionsCache[hash] = data;
        }

        public static void ClearStyleCache()
        {
            foreach (var kvp in s_ComputedStyleCache)
                kvp.Value.Release();

            s_ComputedStyleCache.Clear();
            s_StyleVariableContextCache.Clear();
            s_ComputedTransitionsCache.Clear();
        }
    }

    [VisibleToOtherModules("UnityEditor.UIBuilderModule")]
    internal class VisualTreeStyleUpdater : VisualTreeStyleUpdater<VisualTreeStyleUpdaterTraversal, NoOpStyleProfiler>
    {
    }

    [VisibleToOtherModules("UnityEditor.UIBuilderModule")]
    internal class VisualTreeStyleUpdater<TTraversal, TProfiler> : BaseVisualTreeUpdater, IStyleFeatureChangeReceiver
        where TTraversal : VisualTreeStyleUpdaterTraversal<TProfiler>, new()
        where TProfiler : struct, IStyleProfiler
    {
        private HashSet<VisualElement> m_TransitionPropertyUpdateList = new HashSet<VisualElement>();
        private uint m_Version = 0;
        private uint m_LastVersion = 0;
        private bool m_InUpdate;

        private TTraversal m_StyleContextHierarchyTraversal = new ();

        public TTraversal traversal
        {
            get => m_StyleContextHierarchyTraversal;
            set
            {
                m_StyleContextHierarchyTraversal = value;
                panel?.visualTree.IncrementVersion(VersionChangeType.Styles | VersionChangeType.StyleSheet | VersionChangeType.Layout | VersionChangeType.Transform);
            }
        }

        private static readonly string s_Description = "UIElements.UpdateStyle";
        private static readonly ProfilerMarker s_ProfilerMarker = new ProfilerMarker(ProfilerCategory.UIToolkit, s_Description);
        public override ProfilerMarker profilerMarker => s_ProfilerMarker;

        public void DirtyStyleSheets()
        {
            visualTree.IncrementVersion(VersionChangeType.StyleSheet); // dirty all styles
        }


        public override void OnVersionChanged(VisualElement ve, VersionChangeType versionChangeType)
        {
            if ((versionChangeType & (VersionChangeType.StyleSheet | VersionChangeType.TransitionProperty)) == 0)
                return;

            ++m_Version;

            if ((versionChangeType & VersionChangeType.StyleSheet) != 0)
            {
                m_StyleContextHierarchyTraversal.AddChangedElement(ve);
            }

            if ((versionChangeType & VersionChangeType.TransitionProperty) != 0)
            {
                m_TransitionPropertyUpdateList.Add(ve);
            }
        }

        public void OnStyleFeaturesChanged(VisualElement ve, ReadOnlySpan<int> changedIds, VersionChangeType versionChangeType)
        {
            if ((versionChangeType & VersionChangeType.TransitionProperty) != 0)
            {
                OnVersionChanged(ve, versionChangeType);
                return;
            }

            ++m_Version;
            m_StyleContextHierarchyTraversal.AddChangedElement(ve, changedIds);
        }

        public override void Update()
        {
            // ApplyStyles dispatches CustomStyleResolvedEvent, and a handler can reach ValidateLayout; Repaint
            // and UpdateWithoutRepaint run this phase outside ValidateLayout's own re-entrancy guard.
            if (m_Version == m_LastVersion || m_InUpdate)
                return;

            m_InUpdate = true;
            try
            {
                m_LastVersion = m_Version;
                if (StyleInvalidationSettings.s_UseInvalidationSets)
                    panel?.styleInvalidationIndex.Refresh();
                ApplyStyles();

                var animSystem = panel?.GetUpdater(VisualTreeUpdatePhase.Animation) as VisualElementAnimationSystem;
                if (animSystem != null)
                {
                    // Before the re-apply below, so a clip removed by this pass drops its binder
                    // instead of having its last sampled value painted over the new style.
                    animSystem.FlushDirtyElementClips();

                    // Style resolution replaces computed styles wholesale via SetComputedStyle, which
                    // overwrites values that UIAnimationBinder applied via ApplyPropertyAnimation.
                    // CSS transitions are handled by ForceUpdateTransitions inside TraverseRecursive,
                    // but animation binders have no equivalent. Re-apply the last sampled binder values
                    // so they survive style re-resolution.
                    if (animSystem.hasActiveAnimationBinders)
                        animSystem.ReapplyAnimationBinderValues();
                }

                m_StyleContextHierarchyTraversal.Clear();

                foreach (var ve in m_TransitionPropertyUpdateList)
                {
                    // Allow for transitions to be cancelled if matching transition property was removed.
                    if (ve.hasRunningOrCompletedAnimations)
                    {
                        ComputedTransitionUtils.UpdateComputedTransitions(ref ve.computedStyle, out var computedTransitions);
                        m_StyleContextHierarchyTraversal.CancelAnimationsWithNoTransitionProperty(computedTransitions, ve, ref ve.computedStyle);
                    }
                }
                m_TransitionPropertyUpdateList.Clear();
            }
            finally
            {
                m_InUpdate = false;
            }
        }

        protected bool disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
                m_StyleContextHierarchyTraversal.Clear();

            disposed = true;
        }

        protected void ApplyStyles()
        {
            Debug.Assert(visualTree.panel != null);
            m_StyleContextHierarchyTraversal.PrepareTraversal(panel, panel.scaledPixelsPerPoint);
            m_StyleContextHierarchyTraversal.Traverse(visualTree);
        }
    }

    [VisibleToOtherModules("UnityEditor.UIBuilderModule")]
    class StyleMatchingContext
    {
        private List<StyleSheet> m_StyleSheetStack;
        private List<SelectorAccelerationCacheEntry> m_CacheEntryStack;

        public int styleSheetCount => m_StyleSheetStack.Count;

        public StyleVariableContext variableContext;
        public VisualElement currentElement;
        // When true, MatchRightToLeft writes triggerPseudoMask/dependencyPseudoMask back onto
        // the visited elements. The cold-path callers (inspector, debugger) pass false to leave
        // element state untouched.
        public readonly bool applyPseudoMasks;
        public AncestorFilter ancestorFilter = new AncestorFilter();

        // Grow-only scratch for the native matcher's matched-descriptor indices. A descriptor
        // can match at most once per element x sheet call, so a sheet's descriptor count bounds
        // the output. Matching is single-threaded per context, so the buffer is reused per call.
        int[] m_MatchedIndicesScratch;

        internal int[] GetMatchedIndicesScratch(int minLength)
        {
            if (m_MatchedIndicesScratch == null || m_MatchedIndicesScratch.Length < minLength)
                m_MatchedIndicesScratch = new int[Mathf.NextPowerOfTwo(minLength)];
            return m_MatchedIndicesScratch;
        }

        public StyleMatchingContext(bool applyPseudoMasks)
        {
            m_StyleSheetStack = new List<StyleSheet>();
            m_CacheEntryStack = new List<SelectorAccelerationCacheEntry>();
            variableContext = StyleVariableContext.none;
            currentElement = null;
            this.applyPseudoMasks = applyPseudoMasks;
        }

        public void AddStyleSheet(StyleSheet sheet)
        {
            if (sheet == null)
                return;

            // First point at which the sheet is actually used for styling: validate its serialization
            // layout here rather than on load (guarded to run once per sheet).
            sheet.EnsureSerializationLayoutChecked();

            m_StyleSheetStack.Add(sheet);
            m_CacheEntryStack.Add(SelectorAccelerationCache.shared.GetOrCreate(sheet));
        }

        public void RemoveStyleSheetRange(int index, int count)
        {
            m_StyleSheetStack.RemoveRange(index, count);
            m_CacheEntryStack.RemoveRange(index, count);
        }

        // Returns the context to its initial state, dropping anything an aborted traversal left behind.
        public void Reset()
        {
            m_StyleSheetStack.Clear();
            m_CacheEntryStack.Clear();
            variableContext = StyleVariableContext.none;
            currentElement = null;
            ancestorFilter.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StyleSheet GetStyleSheetAt(int index)
        {
            return m_StyleSheetStack[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SelectorAccelerationCacheEntry GetCacheEntryAt(int index)
        {
            return m_CacheEntryStack[index];
        }
    }

    [VisibleToOtherModules("UnityEditor.UIBuilderModule")]
    internal class VisualTreeStyleUpdaterTraversal : VisualTreeStyleUpdaterTraversal<NoOpStyleProfiler> { }

    [VisibleToOtherModules("UnityEditor.UIBuilderModule")]
    internal class VisualTreeStyleUpdaterTraversal<TStyleProfiler> : HierarchyTraversal where TStyleProfiler : struct, IStyleProfiler
    {
        private StyleVariableContext m_ProcessVarContext = new StyleVariableContext();

        private List<StyleSelectorMatch> m_TempMatchResults = new List<StyleSelectorMatch>();

        private List<VisualElement> m_CustomStyleResolvedElements;

        private float currentPixelsPerPoint { get; set; } = 1.0f;
        // Re-entrancy protection: set to true during style traversal, false before event dispatch
        private bool m_IsApplyingStyles;
        private List<QueuedStyleChange> m_ApplyStyleUpdateList = new List<QueuedStyleChange>();

        private struct QueuedStyleChange
        {
            public VisualElement element;
            public StyleDirtyFlags flags;
            public PendingInvalidationSet pendingSet;
        }

        // Pending descendant invalidation sets attached to elements flagged StyleDirtyFlags.HasPendingSet.
        private readonly Dictionary<VisualElement, PendingInvalidationSet> m_PendingSets = new Dictionary<VisualElement, PendingInvalidationSet>();
        private readonly Stack<PendingInvalidationSet> m_PendingSetPool = new Stack<PendingInvalidationSet>();
        // Sets of the ancestors on the current traversal path, pushed and popped with the ancestor filter.
        private readonly List<PendingInvalidationSet> m_ActivePendingSets = new List<PendingInvalidationSet>();

        StyleMatchingContext m_StyleMatchingContext = new StyleMatchingContext(applyPseudoMasks: true);
        StylePropertyReader m_StylePropertyReader = new StylePropertyReader();

        private BaseVisualElementPanel currentPanel { get; set; }

        public StyleMatchingContext styleMatchingContext => m_StyleMatchingContext;

        public void PrepareTraversal(BaseVisualElementPanel panel, float pixelsPerPoint)
        {
            currentPanel = panel;
            currentPixelsPerPoint = pixelsPerPoint;
        }

        public override void Traverse(VisualElement element)
        {
            m_CustomStyleResolvedElements = VisualElementListPool.Get();

            try
            {
                // Set flag to true during traversal - any AddChangedElement() calls will be queued
                m_IsApplyingStyles = true;

                try
                {
                    base.Traverse(element);

                    if (StyleInvalidationSettings.s_VerifyInvalidation && StyleInvalidationSettings.s_UseInvalidationSets)
                        VerifyInvalidation(element);
                }
                finally
                {
                    // An exception can abort TraverseRecursive at any depth without unwinding its stacks.
                    // Leftover cache entries point at native memory freed when their style sheet unloads
                    // (UUM-153734), so reset here; this relies on nothing catching mid-traversal.
                    m_StyleMatchingContext.Reset();
                    m_TempMatchResults.Clear();

                    // Sets of elements the traversal didn't reach (detached, or aborted by an exception) are dropped.
                    // An element still flagged HasPendingSet is then restyled as Subtree on the next update.
                    m_ActivePendingSets.Clear();
                    foreach (var set in m_PendingSets.Values)
                        ReturnPendingSet(set);
                    m_PendingSets.Clear();

                    // Set flag to false before dispatching events - user callbacks can directly modify elements
                    m_IsApplyingStyles = false;

                    // Process elements that were queued during traversal (before m_IsApplyingStyles was set to false)
                    // Mark them dirty for next frame
                    ProcessQueuedElements();
                }

                // Dispatch accumulated CustomStyleResolvedEvents
                foreach (var elt in m_CustomStyleResolvedElements)
                {
                    using (var evt = CustomStyleResolvedEvent.GetPooled())
                    {
                        EventDispatchUtilities.SendEventDirectlyToTarget(evt, currentPanel, elt);
                    }
                }
            }
            finally
            {
                VisualElementListPool.Release(m_CustomStyleResolvedElements);
                m_CustomStyleResolvedElements = null;
            }
        }

        public void AddChangedElement(VisualElement ve)
        {
            AddChangedElement(ve, StyleDirtyFlags.Subtree, null);
        }

        // Name or class change: only restyle what selectors referencing changedIds can match.
        public void AddChangedElement(VisualElement ve, ReadOnlySpan<int> changedIds)
        {
            var index = ve.elementPanel?.styleInvalidationIndex;
            if (!StyleInvalidationSettings.s_UseInvalidationSets || index == null || index.requiresSubtreeInvalidation)
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
            // If we're inside style traversal, queue the change to avoid re-entrancy
            if (m_IsApplyingStyles)
            {
                m_ApplyStyleUpdateList.Add(new QueuedStyleChange { element = ve, flags = flags, pendingSet = pendingSet });
            }
            else
            {
                MarkStylesDirty(ve, flags, pendingSet);
            }
        }

        public void ProcessQueuedElements()
        {
            // Move queued changes to the tree for next frame. The list may contain duplicates, which
            // MarkStylesDirty merges.
            foreach (var change in m_ApplyStyleUpdateList)
            {
                MarkStylesDirty(change.element, change.flags, change.pendingSet);
            }
            m_ApplyStyleUpdateList.Clear();
        }

        private void MarkStylesDirty(VisualElement ve, StyleDirtyFlags flags, PendingInvalidationSet pendingSet)
        {
            var current = ve.styleDirtyFlags;
            if ((current & StyleDirtyFlags.Subtree) != 0)
            {
                // A full subtree re-match subsumes any finer invalidation.
                ReturnPendingSet(pendingSet);
            }
            else if ((flags & StyleDirtyFlags.Subtree) != 0)
            {
                if (m_PendingSets.Remove(ve, out var previous))
                    ReturnPendingSet(previous);
                ReturnPendingSet(pendingSet);
                ve.styleDirtyFlags = StyleDirtyFlags.Subtree;
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
                ve.styleDirtyFlags = current | flags;
            }

            PropagateToParents(ve);
        }

        private PendingInvalidationSet RentPendingSet()
        {
            return m_PendingSetPool.Count > 0 ? m_PendingSetPool.Pop() : new PendingInvalidationSet();
        }

        private void ReturnPendingSet(PendingInvalidationSet pendingSet)
        {
            if (pendingSet == null)
                return;
            pendingSet.Reset();
            m_PendingSetPool.Push(pendingSet);
        }

        // True when TraverseRecursive has to visit the element: it, an ancestor's subtree or pending set,
        // or one of its descendants is dirty.
        protected bool NeedsStyleTraversal(VisualElement element)
        {
            if (m_IsVerifyingInvalidation || element.stylesAncestorOfDirty || element.styleDirtyFlags != StyleDirtyFlags.None || m_ActivePendingSets.Count > 0)
                return true;
            var parent = element.hierarchy.parent;
            return parent != null && (parent.styleDirtyFlags & StyleDirtyFlags.Subtree) != 0;
        }

        private bool MatchesActivePendingSets(VisualElement element)
        {
            foreach (var set in m_ActivePendingSets)
            {
                if (set.Matches(element))
                    return true;
            }
            return false;
        }

        public void Clear()
        {
            // Note: we don't need to clear flags here because they're cleared during traversal
            m_TempMatchResults.Clear();
        }

        private void PropagateToParents(VisualElement ve)
        {
            var parent = ve.hierarchy.parent;
            while (parent != null)
            {
                if (parent.stylesAncestorOfDirty)
                {
                    // Already marked, no need to continue up the chain
                    break;
                }

                parent.stylesAncestorOfDirty = true;
                parent = parent.hierarchy.parent;
            }
        }

        public override void TraverseRecursive(VisualElement element, int depth)
        {
            if (m_IsVerifyingInvalidation)
            {
                VerifyRecursive(element, depth);
                return;
            }

            // Skip if element is neither dirty nor ancestor of dirty
            // Note: We check the parent's Subtree flag because children inherit from parent and need updating
            if (!NeedsStyleTraversal(element))
            {
                return;
            }

            var parent = element.hierarchy.parent;
            var dirtyFlags = element.styleDirtyFlags;
            PendingInvalidationSet pendingSet = null;
            if ((dirtyFlags & StyleDirtyFlags.HasPendingSet) != 0 && !m_PendingSets.Remove(element, out pendingSet))
            {
                // The set was dropped by an aborted traversal: fall back to a full subtree re-match.
                dirtyFlags |= StyleDirtyFlags.Subtree;
            }

            bool isSubtreeDirty = (dirtyFlags & StyleDirtyFlags.Subtree) != 0 ||
                (parent != null && (parent.styleDirtyFlags & StyleDirtyFlags.Subtree) != 0);
            bool isDirty = isSubtreeDirty || (dirtyFlags & StyleDirtyFlags.Self) != 0 || MatchesActivePendingSets(element);

            // A subtree re-match recomputes every pseudo mask below, so erase them and set the flag so children
            // inherit it during recursion. A Self re-match leaves the descendants' contributions to the masks
            // in place, so the previous masks are kept and the new matches are added to them.
            PseudoStates previousTriggerPseudoMask = PseudoStates.None;
            PseudoStates previousDependencyPseudoMask = PseudoStates.None;
            unsafe
            {
                ref var selectorData = ref *element.selectorDataPtr;
                if (isSubtreeDirty)
                {
                    element.styleDirtyFlags = StyleDirtyFlags.Subtree;
                    selectorData.triggerPseudoMask = PseudoStates.None;
                    selectorData.dependencyPseudoMask = PseudoStates.None;
                }
                else if (isDirty)
                {
                    previousTriggerPseudoMask = selectorData.triggerPseudoMask;
                    previousDependencyPseudoMask = selectorData.dependencyPseudoMask;
                }
            }
            int previousInheritedStylesHash = element.inheritedStylesHash;
            int previousVariableHash = isDirty && !isSubtreeDirty ? element.variableContext.GetVariableHash() : 0;

            int originalStyleSheetCount = m_StyleMatchingContext.styleSheetCount;
            var elementSheets = element.styleSheetList;
            if (elementSheets != null)
            {
                for (var i = 0; i < elementSheets.Count; i++)
                {
                    m_StyleMatchingContext.AddStyleSheet(elementSheets[i]);
                }
            }

            // Store the number of custom style before processing rules in case an element stop
            // to have matching custom styles the event still need to be sent and only looking
            // at the matched custom styles won't suffice.
            var originalVariableContext = m_StyleMatchingContext.variableContext;
            int originalCustomStyleCount = element.computedStyle.customPropertiesCount;
            if (isDirty)
            {
                m_StyleMatchingContext.currentElement = element;
                StyleSelectorHelper<TStyleProfiler>.FindMatches(m_StyleMatchingContext, m_TempMatchResults, originalStyleSheetCount - 1);
                if (!isSubtreeDirty)
                {
                    unsafe
                    {
                        ref var selectorData = ref *element.selectorDataPtr;
                        selectorData.triggerPseudoMask |= previousTriggerPseudoMask;
                        selectorData.dependencyPseudoMask |= previousDependencyPseudoMask;
                    }
                }
                var newStyle = ProcessMatchedRules(element, m_TempMatchResults);
                newStyle.Acquire();

                if (element.hasInlineStyle)
                {
                    var variableContext = m_StyleMatchingContext.variableContext;
                    var rule = element.inlineStyleAccess.inlineRule.rule;
                    if (rule != null && rule.customPropertiesCount > 0)
                    {
                        variableContext = new StyleVariableContext(m_StyleMatchingContext.variableContext);
                        foreach (var property in rule.properties)
                        {
                            if (!property.isCustomProperty)
                                continue;
                            // Inline styles are always local content
                            var sv = new StyleVariable(property.customNameId, rule.styleSheet, property.values, StyleSheetPriority.Default);
                            variableContext.Add(sv);
                        }
                    }
                    element.inlineStyleAccess.ApplyInlineStyles(ref newStyle, variableContext);
                }

                ComputedTransitionUtils.UpdateComputedTransitions(ref newStyle, out var computedTransitions);

                if (element.hasRunningAnimations &&
                    !ComputedTransitionUtils.SameTransitionProperty(ref element.computedStyle, ref newStyle))
                {
                    CancelAnimationsWithNoTransitionProperty(computedTransitions, element, ref newStyle);
                }

                if (computedTransitions.Length > 0 && element.styleInitialized)
                {
                    // Start the transitions. Note that the element still has its old style at this point.
                    ProcessTransitions(computedTransitions, element, ref element.computedStyle, ref newStyle);

                    // Set entire new computed style but immediately force the animated values to be updated again.
                    element.SetComputedStyle(ref newStyle);
                    ForceUpdateTransitions(element);
                }
                else
                {
                    element.SetComputedStyle(ref newStyle);
                }

                newStyle.Release();
                element.styleInitialized = true;
                element.inheritedStylesHash = element.computedStyle.inheritedData.GetHashCode();
                m_StyleMatchingContext.currentElement = null;
                m_TempMatchResults.Clear();

                // Children of a Self re-match only follow when what they inherit changed.
                if (!isSubtreeDirty && (element.inheritedStylesHash != previousInheritedStylesHash ||
                                        element.variableContext.GetVariableHash() != previousVariableHash))
                {
                    isSubtreeDirty = true;
                    element.styleDirtyFlags = StyleDirtyFlags.Subtree;
                }
            }
            else
            {
                m_StyleMatchingContext.variableContext = element.variableContext;
            }

            // Need to send the custom styles event after the inheritance is resolved because an element
            // may want to read standard styles too (TextInputFieldBase callback depends on it).
            // Accumulate elements that need the event; dispatch at the end to support future native code path.
            // The count gate applies to component [StyleProperty] handlers like everyone else: they only
            // sync when the element's computed style has (or had) custom properties to read.
            if (isDirty && (originalCustomStyleCount > 0 || element.computedStyle.customPropertiesCount > 0) &&
                element.HasSelfEventInterests(CustomStyleResolvedEvent.EventCategory))
            {
                m_CustomStyleResolvedElements.Add(element);
            }

            // A subtree re-match already covers everything the pending set can reach.
            bool pushPendingSet = pendingSet != null && !isSubtreeDirty;
            if (pushPendingSet)
                m_ActivePendingSets.Add(pendingSet);
            m_StyleMatchingContext.ancestorFilter.PushElement(element);

            Recurse(element, depth);

            m_StyleMatchingContext.ancestorFilter.PopElement();
            if (pushPendingSet)
                m_ActivePendingSets.RemoveAt(m_ActivePendingSets.Count - 1);
            ReturnPendingSet(pendingSet);

            // Clear flags after traversing children (so children can see parent's dirty state during traversal)
            element.styleDirtyFlags = StyleDirtyFlags.None;
            element.stylesAncestorOfDirty = false;

            m_StyleMatchingContext.variableContext = originalVariableContext;
            if (m_StyleMatchingContext.styleSheetCount > originalStyleSheetCount)
            {
                m_StyleMatchingContext.RemoveStyleSheetRange(originalStyleSheetCount, m_StyleMatchingContext.styleSheetCount - originalStyleSheetCount);
            }
        }

        private void ProcessTransitions(ComputedTransitionProperty[] computedTransitions, VisualElement element, ref ComputedStyle oldStyle, ref ComputedStyle newStyle)
        {
            for (var i = computedTransitions.Length - 1; i >= 0; i--)
            {
                var t = computedTransitions[i];

                // Need to skip inline styles because they take precedence over USS styles
                if (element.hasInlineStyle && element.inlineStyleAccess.IsValueSet(t.id))
                    continue;

                ComputedStyle.StartAnimation(element, t.id, ref oldStyle, ref newStyle, t.durationMs, t.delayMs,
                    t.easingCurve);
            }
        }

        private void ForceUpdateTransitions(VisualElement element)
        {
            element.styleAnimation.GetAllAnimations(m_AnimatedProperties);
            if (m_AnimatedProperties.Count > 0)
            {
                foreach (var id in m_AnimatedProperties)
                {
                    element.styleAnimation.UpdateAnimation(id);
                }
                m_AnimatedProperties.Clear();
            }
        }

        private readonly List<StylePropertyId> m_AnimatedProperties = new List<StylePropertyId>();
        internal void CancelAnimationsWithNoTransitionProperty(ComputedTransitionProperty[] computedTransitions, VisualElement element, ref ComputedStyle newStyle)
        {
            element.styleAnimation.GetAllAnimations(m_AnimatedProperties);
            foreach (var id in m_AnimatedProperties)
            {
                if (!computedTransitions.HasTransitionProperty(id))
                    element.styleAnimation.CancelAnimation(id);
            }
            m_AnimatedProperties.Clear();
        }

        ComputedStyle ProcessMatchedRules(VisualElement element, List<StyleSelectorMatch> matchingSelectors)
        {
            // SpanSort over the backing array uses RefComparison, skipping the per-comparison
            // struct copy that List<T>.Sort(Comparison<T>) does on this ~24B readonly struct.
            // The span is created once and reused by the three loops below (sort is in-place
            // and no later step here resizes the list).
            var matches = NoAllocHelpers.CreateSpan(matchingSelectors);
            SpanSort.Sort(matches, StyleSelectorMatch.RefComparison);

            Int64 matchingRulesHash = ComputeMatchingRulesHash(element, matches, out var variableContext);
            m_StyleMatchingContext.variableContext = variableContext;
            element.variableContext = variableContext;
            if (StyleInvalidationSettings.s_VerifyInvalidation)
                RecordMatch(element, matchingRulesHash);

            var parent = element.hierarchy.parent;

            // Re-resolve when a cached style's baked-in asset EntityIds were destroyed (e.g. an Addressables
            // bundle unloaded) so the reloaded asset replaces the stale, now-invalid reference.
            if (!StyleCache.TryGetValue(matchingRulesHash, out var resolvedStyles) || ComputedStyleUtility.HasStaleAssetReference(ref resolvedStyles))
            {
                ref var parentStyle = ref parent?.computedStyle != null ? ref parent.computedStyle : ref InitialStyle.Get();
                resolvedStyles = ComputedStyle.Create(ref parentStyle);
                resolvedStyles.matchingRulesHash = matchingRulesHash;

                float dpiScaling = element.scaledPixelsPerPoint;
                for (int i = 0; i < matches.Length; i++)
                {
                    ref readonly var match = ref matches[i];
                    m_StylePropertyReader.SetContext(match.sheet, match.complexSelector, m_StyleMatchingContext.variableContext, dpiScaling);
                    resolvedStyles.ApplyProperties(m_StylePropertyReader, ref parentStyle);
                }

                StyleCache.SetValue(matchingRulesHash, ref resolvedStyles);
            }

            return resolvedStyles;
        }

        private void ProcessMatchedVariables(StyleSheet sheet, StyleRule rule, StyleSheetPriority tier)
        {
            foreach (var property in rule.properties)
            {
                if (property.isCustomProperty)
                {
                    var sv = new StyleVariable(
                        property.customNameId,
                        sheet,
                        property.values,
                        tier
                    );
                    m_ProcessVarContext.Add(sv);
                }
            }
        }

        // Hash of the rules matched by an element, and the variable context it gets, given the parent's variable
        // context in m_StyleMatchingContext. matches must be sorted.
        Int64 ComputeMatchingRulesHash(VisualElement element, ReadOnlySpan<StyleSelectorMatch> matches, out StyleVariableContext variableContext)
        {
            Int64 matchingRulesHash = element.typeNameId;
            // Let current DPI contribute to the hash so cache is invalidated when this changes
            matchingRulesHash = (matchingRulesHash * 397) ^ currentPixelsPerPoint.GetHashCode();

            variableContext = m_StyleMatchingContext.variableContext;
            int oldVariablesHash = variableContext.GetVariableHash();
            int customPropertiesCount = 0;
            for (int i = 0; i < matches.Length; i++)
            {
                customPropertiesCount += matches[i].complexSelector.rule.customPropertiesCount;
            }

            if (customPropertiesCount > 0)
            {
                // Element defines new variables, add the parents variables at the beginning of the processing context
                m_ProcessVarContext.AddInitialRange(variableContext);
            }

            for (int i = 0; i < matches.Length; i++)
            {
                // UUM-87950 Don't use rule.GetHashCode() as it isn't defined on the class and
                // falls back on the base Object.GetHashCode() which have been seen to return the
                // same value for different rules across scene changes. The rule index is a good
                // alternate unique identifier for the rule.

                ref readonly var match = ref matches[i];
                var sheet = match.sheet;
                var ruleIndex = match.complexSelector.ruleIndex;
                var specificity = match.complexSelector.specificity;
                matchingRulesHash = (matchingRulesHash * 397) ^ sheet.contentHash;
                matchingRulesHash = (matchingRulesHash * 397) ^ ruleIndex;
                matchingRulesHash = (matchingRulesHash * 397) ^ specificity;

                var rule = match.complexSelector.rule;
                if (rule.customPropertiesCount > 0)
                {
                    ProcessMatchedVariables(sheet, rule, match.tier);
                }
            }

            var parent = element.hierarchy.parent;
            int inheritedStyleHash = parent?.inheritedStylesHash ?? 0;
            matchingRulesHash = (matchingRulesHash * 397) ^ inheritedStyleHash;

            int variablesHash = oldVariablesHash;
            if (customPropertiesCount > 0)
            {
                variablesHash = m_ProcessVarContext.GetVariableHash();
            }
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

        // StyleInvalidationSettings.s_VerifyInvalidation state.
        // The hash of each element's last re-match is recorded rather than read back from computedStyle, because
        // SetComputedStyle keeps the previous matchingRulesHash when the newly resolved values are equal.
        private sealed class RecordedMatch
        {
            public Int64 matchingRulesHash;
        }

        private const int k_MaxLoggedVerificationFailures = 10;
        private ConditionalWeakTable<VisualElement, RecordedMatch> m_RecordedMatches;
        private StyleMatchingContext m_VerifyMatchingContext;
        private HashSet<VisualElement> m_VerifySkippedElements;
        private bool m_IsVerifyingInvalidation;
        private int m_VerifySkipDepth;
        private int m_VerifyFailureCount;

        private void RecordMatch(VisualElement element, Int64 matchingRulesHash)
        {
            m_RecordedMatches ??= new ConditionalWeakTable<VisualElement, RecordedMatch>();
            m_RecordedMatches.GetOrCreateValue(element).matchingRulesHash = matchingRulesHash;
        }

        // Re-matches the whole tree after a pass and checks that every element holds what a full re-match gives.
        // Runs TraverseRecursive so that subclasses (UI Builder) set up the same matching context, but with a
        // separate context that doesn't write pseudo masks and without applying any style.
        private void VerifyInvalidation(VisualElement root)
        {
            // Changes queued during the pass are applied by the next update: skip those elements and their subtree.
            if (m_ApplyStyleUpdateList.Count > 0)
            {
                m_VerifySkippedElements ??= new HashSet<VisualElement>();
                foreach (var change in m_ApplyStyleUpdateList)
                    m_VerifySkippedElements.Add(change.element);
            }

            m_VerifyMatchingContext ??= new StyleMatchingContext(applyPseudoMasks: false);
            var styleMatchingContext = m_StyleMatchingContext;
            m_StyleMatchingContext = m_VerifyMatchingContext;
            m_IsVerifyingInvalidation = true;
            m_VerifyFailureCount = 0;
            try
            {
                base.Traverse(root);
            }
            finally
            {
                m_IsVerifyingInvalidation = false;
                m_VerifySkipDepth = 0;
                m_VerifySkippedElements?.Clear();
                m_VerifyMatchingContext.Reset();
                m_TempMatchResults.Clear();
                m_StyleMatchingContext = styleMatchingContext;
            }

            if (m_VerifyFailureCount > k_MaxLoggedVerificationFailures)
                Debug.LogError($"Style invalidation verification: {m_VerifyFailureCount - k_MaxLoggedVerificationFailures} more mismatching elements not logged.");
        }

        private void VerifyRecursive(VisualElement element, int depth)
        {
            bool skip = m_VerifySkipDepth > 0 || (m_VerifySkippedElements != null && m_VerifySkippedElements.Contains(element));

            int originalStyleSheetCount = m_StyleMatchingContext.styleSheetCount;
            var elementSheets = element.styleSheetList;
            if (elementSheets != null)
            {
                for (var i = 0; i < elementSheets.Count; i++)
                {
                    m_StyleMatchingContext.AddStyleSheet(elementSheets[i]);
                }
            }

            var originalVariableContext = m_StyleMatchingContext.variableContext;
            if (!skip)
            {
                m_StyleMatchingContext.currentElement = element;
                StyleSelectorHelper<TStyleProfiler>.FindMatches(m_StyleMatchingContext, m_TempMatchResults, originalStyleSheetCount - 1);
                var matches = NoAllocHelpers.CreateSpan(m_TempMatchResults);
                SpanSort.Sort(matches, StyleSelectorMatch.RefComparison);
                var expectedHash = ComputeMatchingRulesHash(element, matches, out var expectedVariableContext);
                m_StyleMatchingContext.currentElement = null;
                m_TempMatchResults.Clear();
                CheckVerifiedElement(element, expectedHash, expectedVariableContext);
            }
            else
            {
                m_VerifySkipDepth++;
            }

            // Descendants are checked against what this element holds, so a missed element is reported once,
            // rather than again for every descendant that inherits from it.
            m_StyleMatchingContext.variableContext = element.variableContext;
            m_StyleMatchingContext.ancestorFilter.PushElement(element);

            Recurse(element, depth);

            m_StyleMatchingContext.ancestorFilter.PopElement();
            if (skip)
                m_VerifySkipDepth--;

            m_StyleMatchingContext.variableContext = originalVariableContext;
            if (m_StyleMatchingContext.styleSheetCount > originalStyleSheetCount)
            {
                m_StyleMatchingContext.RemoveStyleSheetRange(originalStyleSheetCount, m_StyleMatchingContext.styleSheetCount - originalStyleSheetCount);
            }
        }

        private void CheckVerifiedElement(VisualElement element, Int64 expectedHash, StyleVariableContext expectedVariableContext)
        {
            // Elements not re-matched since verification was enabled have nothing to compare against.
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

        private static string GetElementPath(VisualElement element)
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
