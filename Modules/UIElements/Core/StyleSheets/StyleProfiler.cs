// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using Unity.Scripting.LifecycleManagement;
using System;
using System.Runtime.InteropServices;
using UnityEngine.Bindings;

namespace UnityEngine.UIElements.StyleSheets;

// Why an element was re-matched and re-resolved during a style update.
[VisibleToOtherModules("UnityEditor.UIBuilderModule", "UnityEditor.GraphViewModule")]
internal enum StyleRestyleReason
{
    // First style resolution since the element was added to the panel.
    Initial,
    // The element itself was invalidated (class/name/pseudo-state/style sheet/inline style change).
    Self,
    // Only an ancestor was invalidated; the element is restyled because dirtiness propagates down.
    Ancestor,
}

[VisibleToOtherModules("UnityEditor.UIBuilderModule")]
internal interface IStyleProfiler
{
    void BeginMatchingStyleSheet(StyleSheet styleSheet, SelectorAccelerationCacheEntry accelerationCacheEntry);
    void BeginMatchingElement(VisualElement element);
    void EndMatchingStyleSheet(StyleSheet styleSheet);

    // Brackets one native matching call, tightly: the bracket closes as soon as the native
    // loop returns. Begin returns the per-descriptor stats buffer for this sheet's cache entry
    // (length >= its allDescriptors count) while a capture is active, or null to skip
    // collection — a null return must be side-effect free (it also folds the profiler branches
    // out of the matcher for NoOpStyleProfiler), and EndSelectorMatching is only called after
    // a non-null Begin. The profiler is expected to pause its sheet-self-time measurement
    // between the two calls, so matching time — including the stats-collection overhead — is
    // attributed per selector via the buffer, while everything outside the bracket (match
    // materialization included) lands in sheet self time.
    SelectorMatchStatsInfo[] BeginSelectorMatching(in SelectorAccelerationCacheEntry accelerationCacheEntry);
    void EndSelectorMatching();

    // Restyle accounting. Callers only invoke OnElementRestyled/OnElementStyleResolved when
    // isRestyleTrackingEnabled is true; NoOpStyleProfiler returns a constant false so the JIT
    // folds the tracking branches (reason computation included) out of the unprofiled updater.
    bool isRestyleTrackingEnabled { get; }

    // Brackets one VisualTreeStyleUpdater style pass (one Update / ApplyStyles call).
    void BeginStyleUpdate();
    void EndStyleUpdate();

    // An element was re-matched (FindMatches) and its computed style re-applied.
    void OnElementRestyled(StyleRestyleReason reason);

    // A restyled element missed the StyleCache and its ComputedStyle was resolved from its matched rules.
    void OnElementStyleResolved();
}

static class StyleProfilerStorage<TProfilerType> where TProfilerType : struct, IStyleProfiler
{
    [NoAutoStaticsCleanup]
    static TProfilerType s_Instance;

    // Caution: only call this using ref InstanceByRef to avoid copying the struct
    public static ref TProfilerType InstanceByRef => ref s_Instance;
}

[VisibleToOtherModules("UnityEditor.UIBuilderModule", "UnityEditor.UIToolkitAuthoringModule")]
struct NoOpStyleProfiler : IStyleProfiler
{
    public void BeginMatchingStyleSheet(StyleSheet styleSheet, SelectorAccelerationCacheEntry accelerationCacheEntry)
    {
    }

    public void BeginMatchingElement(VisualElement element)
    {
    }

    public void EndMatchingStyleSheet(StyleSheet styleSheet)
    {
    }

    public SelectorMatchStatsInfo[] BeginSelectorMatching(in SelectorAccelerationCacheEntry accelerationCacheEntry) => null;

    public void EndSelectorMatching()
    {
    }

    public bool isRestyleTrackingEnabled => false;

    public void BeginStyleUpdate()
    {
    }

    public void EndStyleUpdate()
    {
    }

    public void OnElementRestyled(StyleRestyleReason reason)
    {
    }

    public void OnElementStyleResolved()
    {
    }
}
