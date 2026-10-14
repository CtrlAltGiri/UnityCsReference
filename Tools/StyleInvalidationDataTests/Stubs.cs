// Minimal stand-ins for the engine types the StyleInvalidationData sources touch, so the
// tests can run without the engine. Only the members used by those sources are provided.

using System;
using System.Collections.Generic;

namespace UnityEngine.Bindings
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    sealed class VisibleToOtherModulesAttribute : Attribute
    {
        public VisibleToOtherModulesAttribute(params string[] modules) {}
    }
}

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Field)]
    sealed class SerializeField : Attribute {}

    public class Object
    {
        public static void DestroyImmediate(Object obj) {}
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new() => new T();
    }
}

namespace UnityEngine.UIElements
{
    public readonly struct UniqueStyleString
    {
        static readonly Dictionary<string, int> s_StringToIndex = new() { { "", 1 } };

        public int id { get; }

        public UniqueStyleString(string s)
        {
            if (s == null)
            {
                id = 0;
                return;
            }
            if (!s_StringToIndex.TryGetValue(s, out var value))
            {
                value = s_StringToIndex.Count + 1;
                s_StringToIndex.Add(s, value);
            }
            id = value;
        }
    }

    internal class StyleComplexSelector
    {
        public StyleSelector[] selectors { get; internal set; } = Array.Empty<StyleSelector>();
    }

    internal class StyleRule
    {
        internal StyleRule(StyleSheet styleSheet) {}
        public StyleComplexSelector[] complexSelectors { get; private set; } = Array.Empty<StyleComplexSelector>();
        internal void SetSelectors(StyleComplexSelector[] selectors) => complexSelectors = selectors;
    }

    public class StyleSheet : ScriptableObject
    {
        internal StyleRule[] rules { get; private set; } = Array.Empty<StyleRule>();
        internal List<StyleSheet> flattenedRecursiveImports { get; } = new();
        internal void SetRules(StyleRule[] newRules) => rules = newRules;
    }
}
