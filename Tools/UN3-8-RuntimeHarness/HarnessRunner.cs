using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UN3_8Harness
{
    internal class HarnessOwner : ScriptableObject { }

    // What a UITK editor test suite's [SetUpFixture] does (see PR): verify every pruned pass.
    internal static class HarnessTestSetup
    {
        public static void EnableVerification()
        {
            StyleInvalidationSettings.s_VerifyInvalidation = true;
            StyleInvalidationSettings.verificationFailureCount = 0;
        }
    }

    public static class HarnessRunner
    {
        static readonly string[] k_Classes = { "a", "b", "c", "dark", "swatch", "x", "big", "unused" };
        const string P = "[UN3-8] ";

        static Panel s_PortedPanel, s_StockPanel;
        static PortedStyleUpdater s_Ported;
        static IVisualTreeUpdater s_Stock;
        static List<VisualElement> s_PortedElements, s_StockElements;
        static readonly List<string> s_VerifyErrors = new List<string>();
        static int s_Failures;

        static void Check(bool ok, string what)
        {
            Debug.Log(P + (ok ? "PASS " : "FAIL ") + what);
            if (!ok) s_Failures++;
        }

        public static void Run()
        {
            Application.logMessageReceived += OnLog;
            try
            {
                RunAll();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                s_Failures++;
            }
            Application.logMessageReceived -= OnLog;
            Debug.Log(P + "RESULT: " + (s_Failures == 0 ? "PASS" : $"FAIL ({s_Failures} failed checks)"));
            EditorApplication.Exit(s_Failures == 0 ? 0 : 1);
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error && condition.StartsWith("Style invalidation verification"))
                s_VerifyErrors.Add(condition);
        }

        static void RunAll()
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/UN3_8Harness/harness.uss");
            if (sheet == null) throw new Exception("harness.uss not imported");

            s_PortedPanel = EditorPanel.FindOrCreate(ScriptableObject.CreateInstance<HarnessOwner>());
            s_StockPanel = EditorPanel.FindOrCreate(ScriptableObject.CreateInstance<HarnessOwner>());
            s_Ported = new PortedStyleUpdater();
            s_PortedPanel.SetUpdater(s_Ported, VisualTreeUpdatePhase.Styles);
            s_Stock = s_StockPanel.GetUpdater(VisualTreeUpdatePhase.Styles);

            s_PortedElements = BuildTree(s_PortedPanel.visualTree, sheet);
            s_StockElements = BuildTree(s_StockPanel.visualTree, sheet);
            Debug.Log(P + $"tree: {s_PortedElements.Count} elements per panel, stock updater = {s_Stock.GetType().FullName}, ported = {s_Ported.GetType().FullName}");

            // Test-suite setup hook equivalent.
            HarnessTestSetup.EnableVerification();
            Update(out _);
            Check(SameStyles(), "initial style pass: ported and stock 6000.3 computed styles identical");

            ScenarioPrunedEquivalence();
            ScenarioKillSwitch();
            ScenarioInjectedUnderInvalidation();
        }

        static List<VisualElement> BuildTree(VisualElement visualTree, StyleSheet sheet)
        {
            var list = new List<VisualElement>();
            var root = new VisualElement { name = "harness-root" };
            root.AddToClassList("root");
            root.styleSheets.Add(sheet);
            list.Add(root);
            for (int g = 0; g < 6; g++)
            {
                var group = new VisualElement { name = "group" + g };
                root.Add(group); list.Add(group);
                for (int i = 0; i < 4; i++)
                {
                    var row = new VisualElement();
                    row.AddToClassList(i % 2 == 0 ? "c" : "swatch");
                    group.Add(row); list.Add(row);
                    var label = new Label("l" + i);
                    label.AddToClassList("b");
                    row.Add(label); list.Add(label);
                    var inner = new VisualElement();
                    inner.AddToClassList("c");
                    if (i == 3) inner.AddToClassList("b");
                    row.Add(inner); list.Add(inner);
                    var leaf = new VisualElement();
                    leaf.AddToClassList(i % 2 == 0 ? "swatch" : "b");
                    inner.Add(leaf); list.Add(leaf);
                }
            }
            visualTree.Add(root);
            return list;
        }

        static HashSet<VisualElement> StockPendingUpdateList()
        {
            var updater = (VisualTreeStyleUpdater)s_Stock;
            var field = typeof(VisualTreeStyleUpdaterTraversal).GetField("m_UpdateList", BindingFlags.Instance | BindingFlags.NonPublic);
            return new HashSet<VisualElement>((HashSet<VisualElement>)field.GetValue(updater.traversal));
        }

        // Runs one style pass on both panels. Returns the stock 6000.3 re-match set mapped to ported indices.
        static void Update(out HashSet<int> stockRematched)
        {
            var stockSet = StockPendingUpdateList();
            stockRematched = new HashSet<int>();
            for (int i = 0; i < s_StockElements.Count; i++)
                if (stockSet.Contains(s_StockElements[i])) stockRematched.Add(i);
            s_Ported.Update();
            s_Stock.Update();
        }

        static HashSet<int> PortedRematched()
        {
            var set = new HashSet<int>();
            foreach (var e in s_Ported.traversal.rematchedLastPass)
            {
                int i = s_PortedElements.IndexOf(e);
                if (i >= 0) set.Add(i);
            }
            return set;
        }

        static string Signature(VisualElement e)
        {
            ref var s = ref e.computedStyle;
            return $"w={s.width} h={s.height} ml={s.marginLeft} pt={s.paddingTop} pl={s.paddingLeft} op={s.opacity} fs={s.fontSize} col={s.color} bg={s.backgroundColor} bl={s.borderLeftWidth}";
        }

        static bool SameStyles()
        {
            int diffs = 0;
            for (int i = 0; i < s_PortedElements.Count; i++)
            {
                var a = Signature(s_PortedElements[i]);
                var b = Signature(s_StockElements[i]);
                if (a != b && diffs++ < 3)
                    Debug.Log(P + $"  style differs at {PortedTraversal.GetElementPath(s_PortedElements[i])}: ported {a} / stock {b}");
            }
            return diffs == 0;
        }

        static void Mutate(System.Random rng, out string description)
        {
            int idx = rng.Next(s_PortedElements.Count);
            var p = s_PortedElements[idx];
            var s = s_StockElements[idx];
            if (rng.Next(8) == 0)
            {
                var n = p.name == "hero" ? "" : "hero";
                p.name = n; s.name = n;
                description = $"rename [{idx}] to '{n}'";
            }
            else
            {
                var c = k_Classes[rng.Next(k_Classes.Length)];
                p.ToggleInClassList(c); s.ToggleInClassList(c);
                description = $"toggle .{c} on [{idx}]";
            }
        }

        static void ScenarioPrunedEquivalence()
        {
            Debug.Log(P + "--- scenario 1: random class/name mutations, invalidation sets ON, verify ON ---");
            var rng = new System.Random(1234);
            int portedTotal = 0, stockTotal = 0, styleMismatchPasses = 0, steps = 300;
            int before = StyleInvalidationSettings.verificationFailureCount;
            for (int step = 0; step < steps; step++)
            {
                int n = 1 + rng.Next(3);
                for (int k = 0; k < n; k++) Mutate(rng, out _);
                Update(out var stockSet);
                portedTotal += s_Ported.traversal.rematchedLastPass.Count;
                stockTotal += stockSet.Count;
                if (!SameStyles()) styleMismatchPasses++;
            }
            Debug.Log(P + $"{steps} passes: ported re-matched {portedTotal} elements, stock 6000.3 re-matched {stockTotal} ({100.0 * portedTotal / Math.Max(1, stockTotal):F1}%), verifier checked {s_Ported.traversal.verifiedElementsLastPass} elements on the last pass");
            Check(styleMismatchPasses == 0, $"computed styles identical to stock after every pass (mismatching passes: {styleMismatchPasses})");
            Check(StyleInvalidationSettings.verificationFailureCount == before, $"verify mode reported no mismatches on correct invalidation ({StyleInvalidationSettings.verificationFailureCount - before})");
            Check(portedTotal < stockTotal, "invalidation sets actually pruned re-matching");
        }

        static void ScenarioKillSwitch()
        {
            Debug.Log(P + "--- scenario 2: kill switch s_UseInvalidationSets ---");
            int total = s_PortedElements.Count;

            // Pruned baseline: toggling an unreferenced class re-matches nothing.
            s_PortedElements[1].ToggleInClassList("unused"); s_StockElements[1].ToggleInClassList("unused");
            Update(out _);
            int prunedUnused = PortedRematched().Count;
            Debug.Log(P + $"sets ON: toggle .unused on group0 -> ported re-matched {prunedUnused}");
            Check(prunedUnused == 0, "sets ON: unreferenced class change re-matches nothing");

            StyleInvalidationSettings.s_UseInvalidationSets = false;
            Update(out var stockForced);
            int forced = PortedRematched().Count;
            Debug.Log(P + $"flip OFF -> forced pass re-matched {forced}/{total} harness elements (stock panel, also forced: {stockForced.Count})");
            Check(forced == total, "flip OFF forces one full restyle");
            int passes = s_Ported.traversal.passCount;
            Update(out _);
            Check(s_Ported.traversal.passCount == passes, "flip OFF: only one forced restyle (next update has nothing to do)");

            var rng = new System.Random(99);
            int mismatchedSets = 0, mismatchedStyles = 0, steps = 100;
            for (int step = 0; step < steps; step++)
            {
                Mutate(rng, out var desc);
                Update(out var stockSet);
                var portedSet = PortedRematched();
                if (!portedSet.SetEquals(stockSet))
                {
                    if (mismatchedSets++ < 3)
                        Debug.Log(P + $"  {desc}: ported re-matched {portedSet.Count}, stock {stockSet.Count}");
                }
                if (!SameStyles()) mismatchedStyles++;
            }
            Check(mismatchedSets == 0, $"sets OFF: re-matched element set identical to stock 6000.3 full-subtree behaviour for {steps} mutations (mismatches: {mismatchedSets})");
            Check(mismatchedStyles == 0, "sets OFF: computed styles identical to stock");

            s_PortedElements[1].ToggleInClassList("unused"); s_StockElements[1].ToggleInClassList("unused");
            Update(out _);
            int offUnused = PortedRematched().Count;
            Debug.Log(P + $"sets OFF: toggle .unused on group0 -> ported re-matched {offUnused} (its whole subtree)");
            Check(offUnused == 1 + 4 * 4, "sets OFF: unreferenced class change re-matches the whole subtree, like today");

            StyleInvalidationSettings.s_UseInvalidationSets = true;
            Update(out _);
            forced = PortedRematched().Count;
            Debug.Log(P + $"flip ON -> forced pass re-matched {forced}/{total}");
            Check(forced == total, "flip ON forces one full restyle");
            s_PortedElements[1].ToggleInClassList("unused"); s_StockElements[1].ToggleInClassList("unused");
            Update(out _);
            Check(PortedRematched().Count == 0, "flip ON: pruning is back");
            Check(SameStyles(), "after flipping back ON styles identical to stock");
        }

        static void ScenarioInjectedUnderInvalidation()
        {
            Debug.Log(P + "--- scenario 3: injected under-invalidation (index drops descendant entry for '.a') ---");
            HarnessFaults.s_DropDescendantInvalidationFor = ".a";
            int before = StyleInvalidationSettings.verificationFailureCount;
            s_VerifyErrors.Clear();

            // .a .b { width } and .a > .c { height }: descendants of group2 must re-match, but the faulty index
            // only marks group2 itself.
            s_PortedElements[1 + 2 * 17].AddToClassList("a"); s_StockElements[1 + 2 * 17].AddToClassList("a");
            Update(out var stockSet);
            int found = StyleInvalidationSettings.verificationFailureCount - before;
            Debug.Log(P + $"ported re-matched {s_Ported.traversal.rematchedLastPass.Count} (stock {stockSet.Count}); verify mode reported {found} mismatching elements:");
            foreach (var e in s_VerifyErrors)
                Debug.Log(P + "  captured: " + e);
            Check(found > 0, "verify mode catches the injected under-invalidation");
            Check(s_VerifyErrors.Count > 0 && s_VerifyErrors[0].Contains("VisualElement#harness-root") && s_VerifyErrors[0].Contains("> VisualElement#group2"), "mismatch log contains the element path");
            Check(!SameStyles(), "sanity: the injected bug really left stale styles (differs from stock)");

            // With the kill switch off, the same faulty index is harmless.
            StyleInvalidationSettings.s_UseInvalidationSets = false;
            Update(out _);
            before = StyleInvalidationSettings.verificationFailureCount;
            s_PortedElements[1 + 2 * 17].RemoveFromClassList("a"); s_StockElements[1 + 2 * 17].RemoveFromClassList("a");
            Update(out _);
            Check(SameStyles(), "kill switch OFF works around the faulty index: styles identical to stock");
            Check(StyleInvalidationSettings.verificationFailureCount == before, "kill switch OFF: verification is skipped (no pruned pass)");
            StyleInvalidationSettings.s_UseInvalidationSets = true;
            HarnessFaults.s_DropDescendantInvalidationFor = null;
            Update(out _);
        }
    }
}
