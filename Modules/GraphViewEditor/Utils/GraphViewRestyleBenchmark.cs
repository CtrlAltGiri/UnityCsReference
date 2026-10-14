// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System.Collections.Generic;
using UnityEditor.UIElements.Experimental.RestyleStats;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityEditor.Experimental.GraphView
{
    // (c) Hover storm on a GraphView: the pointer sweeps across every node, one hover leave + enter per mutation.
    sealed class GraphViewHoverStormScenario : StyleRestyleBenchmarkScenario
    {
        const int k_Columns = 10;
        const int k_Rows = 10;
        const int k_PortsPerSide = 2;

        sealed class BenchmarkGraphView : GraphView {}

        readonly List<Node> m_Nodes = new();
        Node m_HoveredNode;

        public override string name => "(c) GraphView hover storm";
        public override string description => $"Sweep :hover across {k_Columns * k_Rows} nodes ({k_PortsPerSide} input + {k_PortsPerSide} output ports each), one leave + enter per iteration";
        public override int iterationCount => k_Columns * k_Rows;

        [InitializeOnLoadMethod]
        static void Register()
        {
            StyleRestyleBenchmarks.s_AdditionalScenarios.Add(() => new GraphViewHoverStormScenario());
        }

        public override void Setup(VisualElement root)
        {
            var graphView = new BenchmarkGraphView();
            graphView.style.flexGrow = 1;
            root.Add(graphView);

            for (int row = 0; row < k_Rows; row++)
            {
                for (int column = 0; column < k_Columns; column++)
                {
                    var node = new Node { title = $"Node {row * k_Columns + column}" };
                    for (int p = 0; p < k_PortsPerSide; p++)
                    {
                        var input = node.InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Single, typeof(float));
                        input.portName = $"In {p}";
                        node.inputContainer.Add(input);

                        var output = node.InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(float));
                        output.portName = $"Out {p}";
                        node.outputContainer.Add(output);
                    }
                    node.RefreshExpandedState();
                    node.RefreshPorts();
                    node.SetPosition(new Rect(column * 220, row * 160, 200, 140));
                    graphView.AddElement(node);
                    m_Nodes.Add(node);
                }
            }
        }

        public override void Mutate(int iteration)
        {
            if (m_HoveredNode != null)
                m_HoveredNode.pseudoStates &= ~PseudoStates.Hover;

            m_HoveredNode = m_Nodes[iteration % m_Nodes.Count];
            m_HoveredNode.pseudoStates |= PseudoStates.Hover;
        }

        public override void TearDown()
        {
            m_Nodes.Clear();
            m_HoveredNode = null;
        }
    }
}
