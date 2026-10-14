// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Bindings;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace UnityEditor.UIElements.Experimental.RestyleStats
{
    // One restyle benchmark: Setup builds the tree once, then each Mutate call is followed by a full
    // style/layout pump and measured as one sample.
    [VisibleToOtherModules("UnityEditor.GraphViewModule")]
    abstract class StyleRestyleBenchmarkScenario
    {
        public abstract string name { get; }
        public abstract string description { get; }
        public virtual int iterationCount => StyleRestyleBenchmarks.k_DefaultIterations;

        public abstract void Setup(VisualElement root);
        public abstract void Mutate(int iteration);
        public virtual void TearDown() {}
    }

    [VisibleToOtherModules("UnityEditor.GraphViewModule")]
    struct StyleRestyleBenchmarkResult
    {
        public string name;
        public string description;
        public int elementCount;
        public List<RestyleUpdateStats> samples;

        public double medianMs => Percentile(0.5);
        public double p90Ms => Percentile(0.9);

        double Percentile(double p)
        {
            if (samples == null || samples.Count == 0)
                return 0;
            var times = samples.Select(s => s.timeMs).OrderBy(t => t).ToList();
            return times[Math.Min(times.Count - 1, (int)Math.Ceiling(p * times.Count) - 1)];
        }

        public double MeanCount(Func<RestyleUpdateStats, int> selector) => samples.Count == 0 ? 0 : samples.Average(s => (double)selector(s));
    }

    // Baseline benchmarks for selector-aware style invalidation (UN3-1). Each scenario runs on a
    // private, offscreen EditorPanel so it is independent of open windows.
    [VisibleToOtherModules("UnityEditor.GraphViewModule")]
    static class StyleRestyleBenchmarks
    {
        internal const int k_DefaultIterations = 20;
        internal const int k_WarmupIterations = 3;
        internal const int k_ListViewItemCount = 5000;
        internal static readonly Vector2 k_PanelSize = new Vector2(1280, 800);

        const int k_SettleFrames = 5;
        const int k_MaxPumpsPerSample = 4;
        const string k_MenuPath = "internal:Window/UI Toolkit/Run Style Restyle Benchmarks";
        const string k_OutputArgument = "-restyleBenchmarkOutput";

        // Scenarios contributed by other modules (GraphView), registered on load.
        public static readonly List<Func<StyleRestyleBenchmarkScenario>> s_AdditionalScenarios = new();

        internal static IEnumerable<StyleRestyleBenchmarkScenario> CreateScenarios()
        {
            yield return new ListViewRootClassToggleScenario();
            yield return new RenameElementScenario();
            foreach (var factory in s_AdditionalScenarios)
                yield return factory();
            yield return new AttachStyleSheetScenario();
        }

        [MenuItem(k_MenuPath)]
        static void RunFromMenu()
        {
            var report = ToMarkdown(RunAll());
            EditorGUIUtility.systemCopyBuffer = report;
            Debug.Log(report);
        }

        // Batch mode entry point: -executeMethod UnityEditor.UIElements.Experimental.RestyleStats.StyleRestyleBenchmarks.RunFromCommandLine [-restyleBenchmarkOutput <path>]
        public static void RunFromCommandLine()
        {
            var report = ToMarkdown(RunAll());
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, k_OutputArgument);
            if (index >= 0 && index + 1 < args.Length)
                File.WriteAllText(args[index + 1], report);
            Debug.Log(report);
        }

        public static List<StyleRestyleBenchmarkResult> RunAll()
        {
            var results = new List<StyleRestyleBenchmarkResult>();
            foreach (var scenario in CreateScenarios())
                results.Add(Run(scenario));
            return results;
        }

        public static StyleRestyleBenchmarkResult Run(StyleRestyleBenchmarkScenario scenario)
        {
            var owner = ScriptableObject.CreateInstance<StyleRestyleBenchmarkPanelOwner>();
            owner.hideFlags = HideFlags.HideAndDontSave;
            var panel = new EditorPanel(owner);
            var result = new StyleRestyleBenchmarkResult
            {
                name = scenario.name,
                description = scenario.description,
                samples = new List<RestyleUpdateStats>(),
            };

            try
            {
                var root = panel.visualTree;
                root.SetSize(k_PanelSize);
                UIElementsEditorUtility.AddDefaultEditorStyleSheets(root);

                scenario.Setup(root);
                for (int i = 0; i < k_SettleFrames; i++)
                    Pump(panel);

                result.elementCount = CountElements(root);

                using var recording = RestyleStatsStyleUpdater.Record(panel);
                for (int i = 0; i < k_WarmupIterations + scenario.iterationCount; i++)
                {
                    recording.BeginSample();
                    scenario.Mutate(i);

                    // A mutation can cascade (layout -> geometry callbacks -> class changes), so pump
                    // until no further style update runs and attribute all of them to this mutation.
                    for (int pump = 0; pump < k_MaxPumpsPerSample; pump++)
                    {
                        var before = recording.updates.Count;
                        Pump(panel);
                        if (recording.updates.Count == before)
                            break;
                    }

                    var sample = recording.EndSample();
                    if (i >= k_WarmupIterations)
                        result.samples.Add(sample);
                }
            }
            finally
            {
                scenario.TearDown();
                panel.Dispose();
                Object.DestroyImmediate(owner);
            }

            return result;
        }

        internal static void Pump(BaseVisualElementPanel panel)
        {
            panel.scheduler.UpdateScheduledEvents();
            ((Panel)panel).UpdateWithoutRepaint();
        }

        static int CountElements(VisualElement element)
        {
            int count = 1;
            for (int i = 0; i < element.hierarchy.childCount; i++)
                count += CountElements(element.hierarchy[i]);
            return count;
        }

        public static string ToMarkdown(List<StyleRestyleBenchmarkResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## UI Toolkit restyle baseline ({Application.unityVersion}, {SystemInfo.operatingSystem})");
            sb.AppendLine();
            sb.AppendLine("Per mutation (mean over measured iterations; times are style-update time only).");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Tree size | Updates | Matched | Self | Ancestor | Initial | Resolved | Median ms | P90 ms |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var r in results)
            {
                sb.AppendLine($"| {r.name} | {r.elementCount} | {r.MeanCount(s => s.updateCount):0.#} | {r.MeanCount(s => s.matchedCount):0.#} | " +
                    $"{r.MeanCount(s => s.selfCount):0.#} | {r.MeanCount(s => s.ancestorCount):0.#} | {r.MeanCount(s => s.initialCount):0.#} | " +
                    $"{r.MeanCount(s => s.resolvedCount):0.#} | {r.medianMs:0.000} | {r.p90Ms:0.000} |");
            }
            sb.AppendLine();
            foreach (var r in results)
                sb.AppendLine($"- **{r.name}**: {r.description} ({r.samples.Count} iterations)");
            return sb.ToString();
        }
    }

    class StyleRestyleBenchmarkPanelOwner : ScriptableObject {}

    // Shared tree for the ListView scenarios: a virtualized 5k-row ListView filling the panel.
    abstract class ListViewRestyleScenario : StyleRestyleBenchmarkScenario
    {
        protected ListView listView { get; private set; }

        public override void Setup(VisualElement root)
        {
            var items = new List<int>(StyleRestyleBenchmarks.k_ListViewItemCount);
            for (int i = 0; i < StyleRestyleBenchmarks.k_ListViewItemCount; i++)
                items.Add(i);

            listView = new ListView(items, 22, () => new Label(), (e, i) => ((Label)e).text = "Row " + i)
            {
                name = "restyle-benchmark-list",
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
            };
            listView.style.flexGrow = 1;
            root.Add(listView);
        }
    }

    // (a) Toggle a class on the root of a 5k-row ListView: no USS rule references the class.
    sealed class ListViewRootClassToggleScenario : ListViewRestyleScenario
    {
        public override string name => "(a) ListView root class toggle";
        public override string description => "EnableInClassList(\"restyle-benchmark--toggled\") on the root of a 5k-row ListView; no rule references the class";

        public override void Mutate(int iteration) => listView.EnableInClassList("restyle-benchmark--toggled", iteration % 2 == 0);
    }

    // (b) Rename an element: SetNameId invalidation path.
    sealed class RenameElementScenario : ListViewRestyleScenario
    {
        public override string name => "(b) Rename element";
        public override string description => "Alternate the name of the 5k-row ListView root between two values no rule references";

        public override void Mutate(int iteration) => listView.name = iteration % 2 == 0 ? "restyle-benchmark-a" : "restyle-benchmark-b";
    }

    // (d) Control: attaching a style sheet must keep restyling the whole subtree.
    sealed class AttachStyleSheetScenario : ListViewRestyleScenario
    {
        StyleSheet m_StyleSheet;

        public override string name => "(d) Attach style sheet (control)";
        public override string description => "Alternately add/remove the default editor theme sheet on the 5k-row ListView root; must not regress";

        public override void Setup(VisualElement root)
        {
            base.Setup(root);
            m_StyleSheet = EditorGUIUtility.isProSkin ? UIElementsEditorUtility.GetCommonLightStyleSheet() : UIElementsEditorUtility.GetCommonDarkStyleSheet();
        }

        public override void Mutate(int iteration)
        {
            if (iteration % 2 == 0)
                listView.styleSheets.Add(m_StyleSheet);
            else
                listView.styleSheets.Remove(m_StyleSheet);
        }
    }
}
