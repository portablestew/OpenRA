#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenRA.Support;

namespace OpenRA.Test
{
	/// <summary>A node with the reference shapes that make the real simulation graph hard to copy.</summary>
	sealed class CloneNode
	{
		public readonly string Name;

		/// <summary>A back-reference, forming a cycle with <see cref="Children"/>.</summary>
		public CloneNode Parent;

		/// <summary>An instance that may be reachable by more than one path.</summary>
		public CloneNode Shared;

		public readonly List<CloneNode> Children = [];
		public int Value;

		public CloneNode(string name) { Name = name; }
	}

	/// <summary>Collections keyed on object identity, as used throughout the simulation traits.</summary>
	sealed class CloneRegistry
	{
		public CloneNode Node;
		public readonly Dictionary<CloneNode, int> ByNode = [];
		public readonly HashSet<CloneNode> Members = [];
	}

	sealed class CloneDelegateHolder
	{
		public CloneNode Node;
		public Func<int> Callback;
	}

	sealed class CloneMulticastHolder
	{
		public readonly List<CloneNode> Nodes = [];
		public Func<int> Callback;
	}

	/// <summary>A value type embedding a reference, mirroring Target holding an Actor.</summary>
	readonly struct CloneRef
	{
		public readonly CloneNode Node;

		public CloneRef(CloneNode node) { Node = node; }
	}

	sealed class CloneStructHolder
	{
		public CloneRef Ref;
		public CloneNode Direct;
	}

	[TestFixture]
	sealed class ObjectGraphClonerTest
	{
		[TestCase(TestName = "Cloning a reference cycle terminates and remaps the back-reference")]
		public void ClonesReferenceCyclesAndRemapsBackReferences()
		{
			var parent = new CloneNode("parent");
			var child = new CloneNode("child");
			parent.Children.Add(child);
			child.Parent = parent;

			var clone = new ObjectGraphCloner().Clone(parent);

			Assert.That(clone, Is.Not.SameAs(parent));
			Assert.That(clone.Children[0], Is.Not.SameAs(child));
			Assert.That(clone.Children[0].Parent, Is.SameAs(clone),
				"A back-reference must point at the cloned parent, not the original.");
		}

		[TestCase(TestName = "An instance reachable by several paths is cloned exactly once")]
		public void ClonesSharedInstancesExactlyOnce()
		{
			var shared = new CloneNode("shared");
			var root = new CloneNode("root");
			root.Children.Add(new CloneNode("a") { Shared = shared });
			root.Children.Add(new CloneNode("b") { Shared = shared });

			var clone = new ObjectGraphCloner().Clone(root);

			Assert.That(clone.Children[0].Shared, Is.Not.SameAs(shared));
			Assert.That(clone.Children[0].Shared, Is.SameAs(clone.Children[1].Shared),
				"Aliasing within the graph must survive cloning, or the copy is not structurally equivalent.");
		}

		[TestCase(TestName = "Mutating a clone does not affect the original graph")]
		public void CloneIsIndependentOfOriginal()
		{
			var child = new CloneNode("child") { Value = 1 };
			var root = new CloneNode("root");
			root.Children.Add(child);

			var clone = new ObjectGraphCloner().Clone(root);
			clone.Children[0].Value = 42;
			clone.Children.Add(new CloneNode("added"));

			Assert.That(child.Value, Is.EqualTo(1), "The original must not observe writes made to the clone.");
			Assert.That(root.Children, Has.Count.EqualTo(1), "The clone must not share its collections with the original.");
		}

		[TestCase(TestName = "Identity-keyed dictionary and set entries are rekeyed onto the clones")]
		public void RemapsIdentityKeyedCollections()
		{
			var node = new CloneNode("keyed");
			var registry = new CloneRegistry { Node = node };
			registry.ByNode.Add(node, 7);
			registry.Members.Add(node);

			var clone = new ObjectGraphCloner().Clone(registry);

			Assert.That(clone.Node, Is.Not.SameAs(node));

			Assert.That(clone.ByNode.ContainsKey(node), Is.False,
				"A cloned dictionary must not remain keyed on instances from the original graph.");
			Assert.That(clone.ByNode.TryGetValue(clone.Node, out var value), Is.True,
				"A lookup using the cloned key must succeed, which requires the dictionary to be rebuilt.");
			Assert.That(value, Is.EqualTo(7));

			Assert.That(clone.Members.Contains(node), Is.False);
			Assert.That(clone.Members.Contains(clone.Node), Is.True);
		}

		[TestCase(TestName = "References held inside a value type are remapped")]
		public void RemapsReferencesInsideStructFields()
		{
			var node = new CloneNode("targeted");
			var holder = new CloneStructHolder { Ref = new CloneRef(node), Direct = node };

			var clone = new ObjectGraphCloner().Clone(holder);

			Assert.That(clone.Direct, Is.Not.SameAs(node));
			Assert.That(clone.Ref.Node, Is.SameAs(clone.Direct),
				"A reference embedded in a struct must be remapped to the same clone as a direct reference.");
		}

		[TestCase(TestName = "A delegate is rebuilt against the cloned closure, not left acting on the original")]
		public void RemapsDelegateClosureOntoTheClone()
		{
			var captured = new CloneNode("captured") { Value = 1 };
			var holder = new CloneDelegateHolder { Node = captured, Callback = () => captured.Value };

			var cloner = new ObjectGraphCloner();
			var clone = cloner.Clone(holder);

			Assert.That(cloner.Unsupported, Is.Empty, "A delegate is now cloneable and must not be reported as unsupported.");
			Assert.That(clone.Callback, Is.Not.Null);

			// The captured node is reachable both directly and through the closure; both must resolve to the
			// single clone, and invoking the copied delegate must read the clone's state, not the original's.
			clone.Node.Value = 42;
			Assert.That(clone.Callback(), Is.EqualTo(42),
				"The cloned delegate still observed the original captured node, so it was not remapped onto the clone.");
			Assert.That(captured.Value, Is.EqualTo(1), "Invoking the clone must not touch the original graph.");
		}

		[TestCase(TestName = "A multicast delegate is rebuilt entry by entry onto the clones")]
		public void RemapsMulticastDelegate()
		{
			var a = new CloneNode("a") { Value = 10 };
			var b = new CloneNode("b") { Value = 20 };
			var holder = new CloneMulticastHolder();
			holder.Nodes.Add(a);
			holder.Nodes.Add(b);
			holder.Callback += () => a.Value;
			holder.Callback += () => b.Value;

			var cloner = new ObjectGraphCloner();
			var clone = cloner.Clone(holder);

			Assert.That(cloner.Unsupported, Is.Empty);
			Assert.That(clone.Callback.GetInvocationList(), Has.Length.EqualTo(2),
				"Both handlers of the multicast delegate must survive the clone.");

			// Each captured node is also held in the list; the closures must resolve to those same clones.
			clone.Nodes[0].Value = 11;
			clone.Nodes[1].Value = 22;
			var results = new List<int>();
			foreach (var d in clone.Callback.GetInvocationList())
				results.Add((int)d.DynamicInvoke());

			Assert.That(results, Is.EqualTo(new[] { 11, 22 }),
				"The multicast entries were not remapped onto the cloned captured nodes.");
			Assert.That(a.Value, Is.EqualTo(10), "The original graph must be untouched.");
			Assert.That(b.Value, Is.EqualTo(20));
		}

		[TestCase(TestName = "A delegate wrapping a static method is shared, not rebuilt")]
		public void SharesStaticDelegate()
		{
			var holder = new CloneDelegateHolder { Callback = StaticCallback };

			var cloner = new ObjectGraphCloner();
			var clone = cloner.Clone(holder);

			Assert.That(cloner.Unsupported, Is.Empty);
			Assert.That(clone.Callback, Is.Not.Null);
			Assert.That(clone.Callback(), Is.EqualTo(7));
		}

		static int StaticCallback() => 7;

		[TestCase(TestName = "Instances matching the share predicate are not cloned")]
		public void SharesInstancesMatchingThePredicate()
		{
			var shared = new CloneNode("immutable");
			var root = new CloneNode("root");
			root.Children.Add(new CloneNode("a") { Shared = shared });

			var cloner = new ObjectGraphCloner(o => o is CloneNode n && n.Name == "immutable");
			var clone = cloner.Clone(root);

			Assert.That(clone.Children[0].Shared, Is.SameAs(shared),
				"Data declared shareable must be aliased, so that immutable assets are not duplicated per fork.");
			Assert.That(clone.Children[0], Is.Not.SameAs(root.Children[0]));
		}

		[TestCase(TestName = "A graph without delegates clones without reporting anything")]
		public void ReportsNothingForACleanGraph()
		{
			var root = new CloneNode("root");
			root.Children.Add(new CloneNode("child") { Parent = root });

			var cloner = new ObjectGraphCloner();
			cloner.Clone(root);

			Assert.That(cloner.Unsupported, Is.Empty);
		}
	}
}
