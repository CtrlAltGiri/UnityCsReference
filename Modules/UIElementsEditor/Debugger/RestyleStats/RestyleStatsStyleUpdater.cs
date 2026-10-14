// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using UnityEngine.Bindings;
using UnityEngine.UIElements;
using UnityEngine.UIElements.StyleSheets;

namespace UnityEditor.UIElements.Experimental.RestyleStats
{
    [VisibleToOtherModules("UnityEditor.GraphViewModule")]
    class RestyleStatsStyleUpdater : VisualTreeStyleUpdater<VisualTreeStyleUpdaterTraversal<RestyleStatsStyleProfiler>, RestyleStatsStyleProfiler>
    {
        // Replaces the panel's style updater with a recording one until the returned scope is disposed.
        // Dirty state lives on the elements, so swapping updaters neither loses nor forces restyles.
        public static RestyleRecordingScope Record(BaseVisualElementPanel panel)
        {
            return new RestyleRecordingScope(panel);
        }
    }

    [VisibleToOtherModules("UnityEditor.GraphViewModule")]
    sealed class RestyleRecordingScope : IDisposable
    {
        readonly BaseVisualElementPanel m_Panel;
        readonly IVisualTreeUpdater m_PreviousUpdater;
        int m_SampleStart;
        bool m_Disposed;

        internal RestyleRecordingScope(BaseVisualElementPanel panel)
        {
            m_Panel = panel;
            m_PreviousUpdater = panel.GetUpdater(VisualTreeUpdatePhase.Styles);
            panel.SetUpdater(new RestyleStatsStyleUpdater(), VisualTreeUpdatePhase.Styles);
            StyleProfilerStorage<RestyleStatsStyleProfiler>.InstanceByRef.StartRecording();
        }

        // Every style update recorded since the scope was created, in order.
        public IReadOnlyList<RestyleUpdateStats> updates => StyleProfilerStorage<RestyleStatsStyleProfiler>.InstanceByRef.updates;

        // Starts a new sample: EndSample sums the updates recorded after this call.
        public void BeginSample()
        {
            m_SampleStart = updates.Count;
        }

        public RestyleUpdateStats EndSample()
        {
            var total = new RestyleUpdateStats();
            var all = updates;
            for (int i = m_SampleStart; i < all.Count; i++)
            {
                var update = all[i];
                total.Add(in update);
            }
            m_SampleStart = all.Count;
            return total;
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;
            m_Disposed = true;

            StyleProfilerStorage<RestyleStatsStyleProfiler>.InstanceByRef.StopRecording();
            m_Panel.SetUpdater(m_PreviousUpdater, VisualTreeUpdatePhase.Styles);
        }
    }
}
