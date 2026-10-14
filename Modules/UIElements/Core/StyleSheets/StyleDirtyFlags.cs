// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using UnityEngine.Bindings;

namespace UnityEngine.UIElements.StyleSheets
{
    // Why an element's styles must be re-matched on the next style update.
    [Flags]
    [VisibleToOtherModules("UnityEditor.UIBuilderModule")]
    internal enum StyleDirtyFlags
    {
        None = 0,
        // Re-match the element only. Its children follow only if its inherited data or variables change.
        Self = 1 << 0,
        // Re-match the element and every descendant.
        Subtree = 1 << 1,
        // A PendingInvalidationSet is attached: descendants whose name, type or class is in it get Self.
        HasPendingSet = 1 << 2,
    }

    // Subject ids of the descendant selectors that a class or name change on an element may start or stop matching.
    internal sealed class PendingInvalidationSet
    {
        private readonly HashSet<int> m_Ids = new HashSet<int>();

        public bool wildcard { get; private set; }

        public void Add(bool addWildcard, IReadOnlyCollection<int> ids)
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
            if (m_Ids.Contains(element.nameId) || m_Ids.Contains(element.typeNameId))
                return true;
            foreach (var classId in element.GetClassesForIteration().GetClassIds())
            {
                if (m_Ids.Contains(classId))
                    return true;
            }
            return false;
        }

        public void Reset()
        {
            m_Ids.Clear();
            wildcard = false;
        }
    }
}
