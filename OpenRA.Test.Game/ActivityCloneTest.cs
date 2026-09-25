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

using NUnit.Framework;
using OpenRA.Activities;
using OpenRA.Support;

namespace OpenRA.Test
{
	/// <summary>
	/// A concrete <see cref="Activity"/> for cloning tests. It exposes the otherwise-protected linked
	/// list backing so the structure of a cloned tree can be inspected, and carries a couple of typed
	/// fields to confirm ordinary activity state survives the copy.
	/// </summary>
	sealed class MockActivity : Activity
	{
		public readonly string Label;
		public int Counter;

		public MockActivity(string label) { Label = label; }

		// SkipDoneActivities-filtered views, as the activity system itself sees them.
		public Activity ChildView => ChildActivity;
		public Activity NextView => NextActivity;

		public MockActivity ChildAs => (MockActivity)ChildActivity;
		public MockActivity NextAs => (MockActivity)NextActivity;
	}

	/// <summary>
	/// Rung 3: cloning real <see cref="Activity"/> objects. An actor mid-order carries an activity
	/// tree of live objects (a two-axis linked list of child and next activities, each with its own
	/// state machine), and forking the world has to duplicate it faithfully. These build trees through
	/// the genuine Queue/QueueChild/Cancel API rather than by hand, so the cloner is tested against the
	/// engine's real linking behaviour. Ticking an activity needs an Actor (and therefore a World), so
	/// that is deferred to the World rungs; here we clone static tree structure and state only.
	/// </summary>
	[TestFixture]
	sealed class ActivityCloneTest
	{
		[TestCase(TestName = "A queued next-activity chain clones structurally")]
		public void ClonesNextChain()
		{
			var head = new MockActivity("head");
			head.Queue(new MockActivity("second"));
			head.Queue(new MockActivity("third"));

			var clone = new ObjectGraphCloner().Clone(head);

			Assert.That(clone, Is.Not.SameAs(head));
			Assert.That(clone.Label, Is.EqualTo("head"));
			Assert.That(clone.NextAs.Label, Is.EqualTo("second"));
			Assert.That(clone.NextAs.NextAs.Label, Is.EqualTo("third"));
			Assert.That(clone.NextAs.NextAs.NextView, Is.Null);

			// The chain is genuinely duplicated, not aliased back to the original.
			Assert.That(clone.NextView, Is.Not.SameAs(head.NextView));
		}

		[TestCase(TestName = "A child-activity stack clones structurally")]
		public void ClonesChildStack()
		{
			var parent = new MockActivity("parent");
			parent.QueueChild(new MockActivity("child"));
			parent.ChildAs.QueueChild(new MockActivity("grandchild"));

			var clone = new ObjectGraphCloner().Clone(parent);

			Assert.That(clone.ChildAs.Label, Is.EqualTo("child"));
			Assert.That(clone.ChildAs.ChildAs.Label, Is.EqualTo("grandchild"));
			Assert.That(clone.ChildView, Is.Not.SameAs(parent.ChildView));
		}

		[TestCase(TestName = "Both axes of the activity tree clone together")]
		public void ClonesBothAxes()
		{
			// A parent with both a queued follow-up and a running child - the shape an actor executing
			// a compound order actually has.
			var parent = new MockActivity("parent");
			parent.QueueChild(new MockActivity("child"));
			parent.Queue(new MockActivity("next"));

			var clone = new ObjectGraphCloner().Clone(parent);

			Assert.That(clone.ChildAs.Label, Is.EqualTo("child"));
			Assert.That(clone.NextAs.Label, Is.EqualTo("next"));
		}

		[TestCase(TestName = "Typed activity state survives the clone and is independent")]
		public void ClonesAndIsolatesActivityState()
		{
			var head = new MockActivity("head") { Counter = 5 };
			head.Queue(new MockActivity("next") { Counter = 9 });

			var clone = new ObjectGraphCloner().Clone(head);
			Assert.That(clone.Counter, Is.EqualTo(5));
			Assert.That(clone.NextAs.Counter, Is.EqualTo(9));

			clone.Counter = 100;
			clone.NextAs.Counter = 200;

			Assert.That(head.Counter, Is.EqualTo(5), "Writing to the clone must not affect the original activity.");
			Assert.That(head.NextAs.Counter, Is.EqualTo(9));
		}

		[TestCase(TestName = "Activity State is preserved, including a cancelled-to-Done queue entry")]
		public void PreservesActivityState()
		{
			// Cancel() on a still-queued activity marks it Done rather than removing it. The activity
			// system relies on SkipDoneActivities to step over such entries. A clone must carry the
			// State field across so the copy skips exactly the same entries.
			var head = new MockActivity("head");
			var doomed = new MockActivity("doomed");
			head.Queue(doomed);
			head.Queue(new MockActivity("survivor"));

			// Null actor is safe here: the entry is still Queued, so Cancel only flips its state and
			// clears its own NextActivity pointer without touching the actor.
			doomed.Cancel(null, keepQueue: true);
			Assert.That(doomed.State, Is.EqualTo(ActivityState.Done), "Precondition: the entry should be Done.");

			var clone = new ObjectGraphCloner().Clone(head);

			// Reading through the filtered getter should skip the Done entry on the clone, exactly as it
			// does on the original.
			Assert.That(head.NextAs.Label, Is.EqualTo("survivor"), "Precondition: original skips the Done entry.");
			Assert.That(clone.NextAs.Label, Is.EqualTo("survivor"),
				"The clone must reproduce the skip, which requires the Done State to be copied.");
		}

		[TestCase(TestName = "An activity tree clones without reporting unsupported members")]
		public void ClonesWithoutUnsupportedMembers()
		{
			// Guards against a future activity field type the cloner cannot handle slipping in
			// unnoticed. Base Activity plus these mocks must clone cleanly today.
			var parent = new MockActivity("parent");
			parent.QueueChild(new MockActivity("child"));
			parent.Queue(new MockActivity("next"));

			var cloner = new ObjectGraphCloner();
			cloner.Clone(parent);

			Assert.That(cloner.Unsupported, Is.Empty,
				"Cloning an activity tree reported unsupported members: " + string.Join(", ", cloner.Unsupported));
		}
	}
}
