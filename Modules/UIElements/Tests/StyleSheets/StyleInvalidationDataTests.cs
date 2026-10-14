// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System.Collections.Generic;
using NUnit.Framework;

namespace UnityEngine.UIElements.Tests
{
    // Standalone tests over hand-written selectors. Only depends on the authoring selector
    // types, so it also runs outside the engine (see Tools/StyleInvalidationDataTests).
    class StyleInvalidationDataTests
    {
        readonly List<StyleSheet> m_Sheets = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var sheet in m_Sheets)
                UnityEngine.Object.DestroyImmediate(sheet);
            m_Sheets.Clear();
        }

        static int Id(string s) => new UniqueStyleString(s).id;

        // Minimal parser for compounds made of `Type`, `.class`, `#name`, `*` and `:pseudo`,
        // joined by whitespace (descendant) or ` > ` (child).
        static StyleComplexSelector Parse(string text)
        {
            var selectors = new List<StyleSelector>();
            var relationship = StyleSelectorRelationship.None;
            foreach (var token in text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == ">")
                {
                    relationship = StyleSelectorRelationship.Child;
                    continue;
                }

                selectors.Add(Compound(token, selectors.Count == 0 ? StyleSelectorRelationship.None : relationship));
                relationship = StyleSelectorRelationship.Descendent;
            }

            return new StyleComplexSelector { selectors = selectors.ToArray() };
        }

        static StyleSelector Compound(string token, StyleSelectorRelationship previousRelationship)
        {
            var parts = new List<StyleSelectorPart>();
            int mask = 0;
            int i = 0;
            while (i < token.Length)
            {
                char prefix = token[i];
                int start = prefix == '.' || prefix == '#' || prefix == ':' ? i + 1 : i;
                int end = prefix == '*' ? i + 1 : start;
                while (end < token.Length && token[end] != '.' && token[end] != '#' && token[end] != ':' && token[end] != '*')
                    end++;
                string value = token.Substring(start, end - start);

                switch (prefix)
                {
                    case '*':
                        parts.Add(StyleSelectorPart.CreateWildCard());
                        break;
                    case '.':
                        parts.Add(WithId(StyleSelectorPart.CreateClass(value)));
                        break;
                    case '#':
                        parts.Add(WithId(StyleSelectorPart.CreateId(value)));
                        break;
                    case ':':
                        parts.Add(StyleSelectorPart.CreatePseudoClass(value));
                        if (value != "hover" && value != "focus" && value != "checked")
                            mask = StyleSelector.InvalidPseudoStateMask;
                        break;
                    default:
                        parts.Add(WithId(StyleSelectorPart.CreateType(value)));
                        break;
                }
                i = end;
            }

            return MakeSelector(parts.ToArray(), previousRelationship, mask);
        }

        static StyleSelector MakeSelector(StyleSelectorPart[] parts, StyleSelectorRelationship previousRelationship, int pseudoStateMask = 0)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].type != StyleSelectorType.ID && parts[i].type != StyleSelectorType.Class && parts[i].type != StyleSelectorType.Type)
                    parts[i].cachedUniqueStyleStringId = -1;
            }

            return new StyleSelector
            {
                parts = parts,
                previousRelationship = previousRelationship,
                pseudoStateMask = pseudoStateMask,
                negatedPseudoStateMask = pseudoStateMask,
            };
        }

        static StyleSelectorPart WithId(StyleSelectorPart part)
        {
            part.cachedUniqueStyleStringId = Id(part.value);
            return part;
        }

        StyleSheet Sheet(params StyleComplexSelector[] complexSelectors)
        {
            var sheet = ScriptableObject.CreateInstance<StyleSheet>();
            m_Sheets.Add(sheet);
            var rules = new StyleRule[complexSelectors.Length];
            for (int i = 0; i < complexSelectors.Length; i++)
            {
                rules[i] = new StyleRule(sheet);
                rules[i].SetSelectors(new[] { complexSelectors[i] });
            }
            sheet.SetRules(rules);
            return sheet;
        }

        StyleSheet Sheet(params string[] selectors)
        {
            var complexSelectors = new StyleComplexSelector[selectors.Length];
            for (int i = 0; i < selectors.Length; i++)
                complexSelectors[i] = Parse(selectors[i]);
            return Sheet(complexSelectors);
        }

        StyleInvalidationData Build(params string[] selectors) => StyleInvalidationData.Build(Sheet(selectors));

        static int[] Ids(params string[] values)
        {
            var ids = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
                ids[i] = Id(values[i]);
            return ids;
        }

        static void AssertSet(InvalidationSet set, string[] names = null, string[] classes = null, string[] types = null,
            bool wildcard = false, bool childOnly = false, bool invalidateSubtree = false)
        {
            Assert.That(set.names, Is.EquivalentTo(Ids(names ?? new string[0])), "names");
            Assert.That(set.classes, Is.EquivalentTo(Ids(classes ?? new string[0])), "classes");
            Assert.That(set.types, Is.EquivalentTo(Ids(types ?? new string[0])), "types");
            Assert.That(set.wildcard, Is.EqualTo(wildcard), "wildcard");
            Assert.That(set.childOnly, Is.EqualTo(childOnly), "childOnly");
            Assert.That(set.invalidateSubtree, Is.EqualTo(invalidateSubtree), "invalidateSubtree");
        }

        [Test]
        public void SimpleClass_AffectsSelfOnly()
        {
            var data = Build(".a");

            Assert.That(data.self, Is.EquivalentTo(Ids("a")));
            Assert.That(data.descendant, Is.Empty);
        }

        [Test]
        public void DescendantClass_AncestorFeatureMapsToSubjectClass()
        {
            var data = Build(".a .b");

            Assert.That(data.self, Is.EquivalentTo(Ids("b")));
            Assert.That(data.descendant.Keys, Is.EquivalentTo(Ids("a")));
            AssertSet(data.descendant[Id("a")], classes: new[] { "b" });
        }

        [Test]
        public void ChildClass_IsChildOnly()
        {
            var data = Build(".a > .b");

            Assert.That(data.self, Is.EquivalentTo(Ids("b")));
            AssertSet(data.descendant[Id("a")], classes: new[] { "b" }, childOnly: true);
        }

        [TestCase(".a > .b", ".a .c")]
        [TestCase(".a .c", ".a > .b")]
        public void ChildAndDescendantContributions_AreNotChildOnly(string first, string second)
        {
            var data = Build(first, second);

            AssertSet(data.descendant[Id("a")], classes: new[] { "b", "c" });
        }

        [Test]
        public void GrandparentThroughChildChain_IsNotChildOnly()
        {
            var data = Build(".x > .a > .b");

            AssertSet(data.descendant[Id("a")], classes: new[] { "b" }, childOnly: true);
            AssertSet(data.descendant[Id("x")], classes: new[] { "b" });
        }

        [Test]
        public void WildcardSubject_SetsWildcard()
        {
            var data = Build(".a *");

            Assert.That(data.self, Is.Empty);
            AssertSet(data.descendant[Id("a")], wildcard: true);
        }

        [Test]
        public void PseudoOnlySubject_SetsWildcard()
        {
            var data = Build(".a > :hover");

            Assert.That(data.self, Is.Empty);
            AssertSet(data.descendant[Id("a")], wildcard: true, childOnly: true);
        }

        [Test]
        public void NameAncestor_MapsToSubjectClass()
        {
            var data = Build("#n .b");

            Assert.That(data.self, Is.EquivalentTo(Ids("b")));
            Assert.That(data.descendant.Keys, Is.EquivalentTo(Ids("n")));
            AssertSet(data.descendant[Id("n")], classes: new[] { "b" });
        }

        [Test]
        public void TypeAndClassCompound_AllFeaturesAffectSelf()
        {
            var data = Build("Button.a");

            Assert.That(data.self, Is.EquivalentTo(Ids("Button", "a")));
            Assert.That(data.descendant, Is.Empty);
        }

        [Test]
        public void SubjectKey_PrefersNameThenClassThenType()
        {
            var data = Build(".p Label.c#n", ".q Label.c", ".r Label");

            AssertSet(data.descendant[Id("p")], names: new[] { "n" });
            AssertSet(data.descendant[Id("q")], classes: new[] { "c" });
            AssertSet(data.descendant[Id("r")], types: new[] { "Label" });
        }

        [Test]
        public void AncestorCompoundFeatures_AllMapToSubject()
        {
            var data = Build("Button.a#n .b");

            Assert.That(data.descendant.Keys, Is.EquivalentTo(Ids("Button", "a", "n")));
            foreach (var set in data.descendant.Values)
                AssertSet(set, classes: new[] { "b" });
        }

        [Test]
        public void Imports_AreIncluded()
        {
            var imported = Sheet(".i .j");
            var main = Sheet(".a");
            main.flattenedRecursiveImports.Add(imported);

            var data = StyleInvalidationData.Build(main, 42);

            Assert.That(data.buildId, Is.EqualTo(42));
            Assert.That(data.self, Is.EquivalentTo(Ids("a", "j")));
            AssertSet(data.descendant[Id("i")], classes: new[] { "j" });
        }

        [Test]
        public void NullImport_IsSkipped()
        {
            var main = Sheet(".a");
            main.flattenedRecursiveImports.Add(null);

            var data = StyleInvalidationData.Build(main);

            Assert.That(data.self, Is.EquivalentTo(Ids("a")));
        }

        [Test]
        public void InvalidPseudoState_FallsBackToSubtree()
        {
            var data = Build(".a .b:not-a-real-state");

            Assert.That(data.self, Is.EquivalentTo(Ids("b")));
            AssertSet(data.descendant[Id("a")], invalidateSubtree: true);
        }

        [Test]
        public void Predicate_FallsBackToSubtree()
        {
            var complex = new StyleComplexSelector
            {
                selectors = new[]
                {
                    MakeSelector(new[] { WithId(StyleSelectorPart.CreateClass("a")) }, StyleSelectorRelationship.None),
                    MakeSelector(new[] { StyleSelectorPart.CreatePredicate(new object()) }, StyleSelectorRelationship.Child),
                }
            };

            var data = StyleInvalidationData.Build(Sheet(complex));

            Assert.That(data.self, Is.Empty);
            AssertSet(data.descendant[Id("a")], childOnly: true, invalidateSubtree: true);
        }

        [Test]
        public void RecursivePseudoClass_FallsBackToSubtree()
        {
            var recursive = new StyleSelectorPart { type = StyleSelectorType.RecursivePseudoClass, value = "hover" };
            var complex = new StyleComplexSelector
            {
                selectors = new[]
                {
                    MakeSelector(new[] { WithId(StyleSelectorPart.CreateClass("a")), recursive }, StyleSelectorRelationship.None),
                    MakeSelector(new[] { WithId(StyleSelectorPart.CreateClass("b")) }, StyleSelectorRelationship.Descendent),
                }
            };

            var data = StyleInvalidationData.Build(Sheet(complex));

            Assert.That(data.self, Is.EquivalentTo(Ids("b")));
            AssertSet(data.descendant[Id("a")], invalidateSubtree: true);
        }

        [Test]
        public void EmptySheet_ProducesEmptyData()
        {
            var data = StyleInvalidationData.Build(Sheet(new string[0]));

            Assert.That(data.self, Is.Empty);
            Assert.That(data.descendant, Is.Empty);
        }
    }
}
