// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine.Bindings;
using UnityEngine.UIElements.StyleSheets;

namespace UnityEditor.UIElements.Experimental.RestyleStats
{
    // Restyle accounting for one VisualTreeStyleUpdater pass (or a sum of passes).
    [VisibleToOtherModules("UnityEditor.GraphViewModule")]
    struct RestyleUpdateStats
    {
        public int initialCount;
        public int selfCount;
        public int ancestorCount;
        // Restyled elements that missed the StyleCache and resolved a new ComputedStyle.
        public int resolvedCount;
        public int updateCount;
        public double timeMs;

        // Every restyled element goes through FindMatches exactly once.
        public int matchedCount => initialCount + selfCount + ancestorCount;

        public int GetCount(StyleRestyleReason reason) => reason switch
        {
            StyleRestyleReason.Initial => initialCount,
            StyleRestyleReason.Self => selfCount,
            _ => ancestorCount,
        };

        public void Add(in RestyleUpdateStats other)
        {
            initialCount += other.initialCount;
            selfCount += other.selfCount;
            ancestorCount += other.ancestorCount;
            resolvedCount += other.resolvedCount;
            updateCount += other.updateCount;
            timeMs += other.timeMs;
        }
    }

    // Editor-only profiler recording RestyleUpdateStats for every style update while isRecording is set.
    [VisibleToOtherModules("UnityEditor.GraphViewModule")]
    struct RestyleStatsStyleProfiler : IStyleProfiler
    {
        List<RestyleUpdateStats> m_Updates;
        RestyleUpdateStats m_Current;
        Stopwatch m_Stopwatch;
        bool m_IsRecording;
        bool m_InUpdate;

        public IReadOnlyList<RestyleUpdateStats> updates => m_Updates;

        public void StartRecording()
        {
            m_Updates ??= new List<RestyleUpdateStats>();
            m_Stopwatch ??= new Stopwatch();
            m_Updates.Clear();
            m_IsRecording = true;
        }

        public void StopRecording()
        {
            m_IsRecording = false;
            m_InUpdate = false;
        }

        public bool isRestyleTrackingEnabled => m_InUpdate;

        public void BeginStyleUpdate()
        {
            if (!m_IsRecording)
                return;

            m_Current = new RestyleUpdateStats { updateCount = 1 };
            m_InUpdate = true;
            m_Stopwatch.Restart();
        }

        public void EndStyleUpdate()
        {
            if (!m_InUpdate)
                return;

            m_Stopwatch.Stop();
            m_Current.timeMs = m_Stopwatch.Elapsed.TotalMilliseconds;
            m_Updates.Add(m_Current);
            m_InUpdate = false;
        }

        public void OnElementRestyled(StyleRestyleReason reason)
        {
            switch (reason)
            {
                case StyleRestyleReason.Initial:
                    m_Current.initialCount++;
                    break;
                case StyleRestyleReason.Self:
                    m_Current.selfCount++;
                    break;
                default:
                    m_Current.ancestorCount++;
                    break;
            }
        }

        public void OnElementStyleResolved()
        {
            m_Current.resolvedCount++;
        }

        public void BeginMatchingStyleSheet(StyleSheet styleSheet, SelectorAccelerationCacheEntry accelerationCacheEntry)
        {
        }

        public void BeginMatchingElement(UnityEngine.UIElements.VisualElement element)
        {
        }

        public void EndMatchingStyleSheet(StyleSheet styleSheet)
        {
        }

        public SelectorMatchStatsInfo[] BeginSelectorMatching(in SelectorAccelerationCacheEntry accelerationCacheEntry) => null;

        public void EndSelectorMatching()
        {
        }
    }
}
