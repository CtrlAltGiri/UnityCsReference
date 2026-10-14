// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using UnityEngine.Bindings;

namespace UnityEngine.UIElements.StyleSheets
{
    // Switches for selector-aware style invalidation (StyleDirtyFlags.Self and HasPendingSet).
    [VisibleToOtherModules("UnityEditor.UIBuilderModule")]
    internal static class StyleInvalidationSettings
    {
        private static bool s_UseInvalidationSetsValue = true;
        private static bool s_VerifyInvalidationValue;

        // Kill switch. When false, every style change re-matches the changed element's whole subtree, as before
        // invalidation sets existed. Changing it restyles every panel once.
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

        // When true, each style pass that ran with invalidation sets is followed by a full re-match of the tree, and
        // every element whose matchingRulesHash or variableContext differs from what the pass left is logged as an
        // error. Disabled by default and enabled from styling tests, like StyleSelectorHelper.s_VerifyBloomIntegrity.
        // Enabling it restyles every panel once so that every element has a recorded match to verify.
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

        // Mismatches found by s_VerifyInvalidation. Tests can reset and read it instead of expecting the logged errors.
        internal static int verificationFailureCount { get; set; }

        private static void ForceFullRestyle()
        {
            var iterator = UIElementsUtility.GetPanelsIterator();
            while (iterator.MoveNext())
                iterator.Current.Value.visualTree.IncrementVersion(VersionChangeType.StyleSheet);
        }
    }
}
