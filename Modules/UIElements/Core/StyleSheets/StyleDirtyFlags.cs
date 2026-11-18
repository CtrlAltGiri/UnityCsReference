// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using UnityEngine.Bindings;

namespace UnityEngine.UIElements
{
    // Why an element must be visited by the style traversal.
    [Flags]
    [VisibleToOtherModules("UnityEditor.UIBuilderModule")]
    internal enum StyleDirtyFlags
    {
        None = 0,
        // Re-match the element itself. Its children follow only if what they inherit changes.
        Self = 1 << 0,
        // Re-match the element and all of its descendants.
        Subtree = 1 << 1,
        // The element has a PendingInvalidationSet: re-match the descendants it selects.
        HasPendingSet = 1 << 2,
    }

    // Descendants of an element that must be re-matched after one of its classes or its name changed:
    // those with a name, type or class in ids, or all of them when wildcard is set.
    internal sealed class PendingInvalidationSet
    {
        static readonly ObjectPool<PendingInvalidationSet> s_Pool = new ObjectPool<PendingInvalidationSet>(() => new PendingInvalidationSet());

        public readonly HashSet<int> ids = new HashSet<int>();
        public bool wildcard;

        public static PendingInvalidationSet Get() => s_Pool.Get();

        public static void Release(PendingInvalidationSet set)
        {
            if (set == null)
                return;
            set.ids.Clear();
            set.wildcard = false;
            s_Pool.Release(set);
        }

        public void Add(bool addWildcard, IReadOnlyCollection<int> subjectIds)
        {
            if (addWildcard)
                wildcard = true;
            if (wildcard || subjectIds == null)
                return;
            foreach (var id in subjectIds)
                ids.Add(id);
        }

        public void UnionWith(PendingInvalidationSet other)
        {
            if (other.wildcard)
                wildcard = true;
            if (!wildcard)
                ids.UnionWith(other.ids);
        }

        public bool Matches(int nameId, int typeNameId, ReadOnlySpan<int> classIds)
        {
            if (wildcard)
                return true;
            if (ids.Count == 0)
                return false;
            if (ids.Contains(nameId) || ids.Contains(typeNameId))
                return true;
            foreach (var classId in classIds)
            {
                if (ids.Contains(classId))
                    return true;
            }
            return false;
        }
    }

    // Lets the style updater receive the ids of the classes / name that changed on an element instead
    // of a plain VersionChangeType.StyleSheet.
    internal interface IStyleFeatureChangeReceiver
    {
        void OnStyleFeaturesChanged(VisualElement ve, VersionChangeType versionChangeType, ReadOnlySpan<int> changedIds);
    }
}
