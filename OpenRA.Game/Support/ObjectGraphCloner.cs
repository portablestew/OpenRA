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
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenRA.Primitives;

namespace OpenRA.Support
{
	/// <summary>
	/// Creates a deep copy of an object graph, remapping every reference within it so that the copy
	/// shares no mutable state with the original.
	/// </summary>
	/// <remarks>
	/// <para>This exists to support forking simulation state: duplicating it so that a copy can be
	/// advanced speculatively and then discarded. The requirements that make this more than a field
	/// copy are that an instance reachable by several paths is cloned exactly once (so reference
	/// identity within the graph is preserved), that reference cycles terminate, and that collections
	/// keyed on object identity are rebuilt rather than copied field-wise.</para>
	/// <para>Delegates are cloned by remapping their target. A delegate is a (target, method) pair; a
	/// lambda's captured variables live in a compiler-generated closure object that is its target. That
	/// closure is cloned through this same graph (so a variable it captured which is elsewhere in the
	/// graph resolves to the one clone, preserving identity), and the delegate is rebuilt against the
	/// clone. Multicast delegates are rebuilt entry by entry. A delegate with no target (a static method)
	/// is shared. This is what lets simulation events and callbacks - <c>World.ActorAdded</c>, condition
	/// notifiers, actor-map triggers - come across a fork wired to the copy rather than the original.</para>
	/// <para>A delegate whose target lies outside the cloned graph (for instance a closure capturing a
	/// service the fork should not inherit) is still cloned structurally; the <see cref="shareInstance"/>
	/// policy is the tool for aliasing such a captured instance instead. Anything genuinely uncloneable
	/// (a multidimensional array) is recorded in <see cref="Unsupported"/> and left null, so that callers
	/// find out rather than inheriting a corrupt copy.</para>
	/// </remarks>
	public sealed class ObjectGraphCloner
	{
		const BindingFlags FieldFlags =
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

		static readonly ConcurrentCache<Type, bool> StructHoldsReferences = new(ComputeStructHoldsReferences);
		static readonly ConcurrentCache<Type, FieldInfo[]> FieldsByType = new(ComputeFields);
		static readonly ConcurrentCache<Type, MethodInfo> HashSetAddMethod = new(ComputeHashSetAddMethod);

		readonly Dictionary<object, object> clones = new(ReferenceEqualityComparer.Instance);
		readonly Func<object, bool> shareInstance;
		readonly List<string> unsupported = [];

		/// <summary>
		/// Whether diagnostic field paths are built while cloning. They cost an allocation per field and
		/// only matter when a value fails to clone, so <see cref="Clone{T}"/> enables them for a retry
		/// that runs only in that case.
		/// </summary>
		bool trackPaths;

		/// <summary>
		/// Field paths at which a value could not be cloned and was left null in the copy.
		/// A successful clone leaves this empty.
		/// </summary>
		public IReadOnlyList<string> Unsupported => unsupported;

		/// <summary>
		/// The number of distinct instances registered as clones. A diagnostic for sizing the cost of a
		/// clone (how large the state graph is) and for reasoning about per-object cloner performance;
		/// it is not otherwise used by the cloner.
		/// </summary>
		public int ClonedObjectCount { get; private set; }

		/// <summary>Creates a cloner with an optional policy for instances that should be shared.</summary>
		/// <param name="shareInstance">
		/// Returns true for instances that should be shared with the original rather than cloned. Used
		/// for immutable or externally owned data, such as rules, map and asset definitions.
		/// </param>
		public ObjectGraphCloner(Func<object, bool> shareInstance = null)
		{
			this.shareInstance = shareInstance ?? (_ => false);
		}

		public T Clone<T>(T root)
		{
			trackPaths = false;
			var clone = (T)CloneValue(root, null);

			// If any value failed to clone, re-run with path tracking to attribute each failure to its
			// field path. The retry is deterministic and fails on the same values again, so the paths
			// reported describe the original graph.
			if (unsupported.Count > 0)
			{
				unsupported.Clear();
				clones.Clear();
				ClonedObjectCount = 0;
				trackPaths = true;
				clone = (T)CloneValue(root, typeof(T).Name);
			}

			return clone;
		}

		object CloneValue(object value, string path)
		{
			if (value == null)
				return null;

			var type = value.GetType();

			// Immutable, so safe to share.
			if (type.IsPrimitive || type.IsEnum || value is string)
				return value;

			if (value is Delegate del)
				return CloneDelegate(del, type, path);

			if (shareInstance(value))
				return value;

			// Structs are copied by value, so they are never registered as clones, but any references
			// they carry still have to be remapped. Target is the motivating case: a value type that
			// embeds an Actor reference.
			if (type.IsValueType)
			{
				if (StructHoldsReferences[type])
					CloneFieldsInto(value, value, type, path);

				return value;
			}

			// Registered before recursing, so that a cycle back to this instance resolves to the clone.
			if (clones.TryGetValue(value, out var existing))
				return existing;

			if (value is Array array)
				return CloneArray(array, type, path);

			// A read-only wrapper (e.g. ReadOnlyDictionary) has no parameterless constructor and, more
			// importantly, must not be rebuilt by re-insertion: it wraps a backing collection that is
			// mutated elsewhere in the graph (Actor's condition cache is the motivating case), and the
			// wrapper has to keep wrapping *that* instance's clone. Field-wise cloning does exactly that -
			// its inner-collection field resolves to the already-registered clone of the backing store -
			// so let such wrappers fall through to the general reference path below.
			if (value is IDictionary dictionary && !dictionary.IsReadOnly)
				return CloneDictionary(dictionary, type, path);

			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>))
				return CloneHashSet(value, type, path);

			var clone = RuntimeHelpers.GetUninitializedObject(type);
			RegisterClone(value, clone);
			CloneFieldsInto(value, clone, type, path);
			return clone;
		}

		object CloneDelegate(Delegate del, Type type, string path)
		{
			// A delegate is immutable, but the closure it points at is not, so two delegate instances
			// wrapping the same target+method must map to one rebuilt delegate to keep reference identity
			// (e.g. the same handler subscribed to two events). Register before rebuilding for that reason
			// and to terminate a target that cycles back to the delegate.
			if (clones.TryGetValue(del, out var existing))
				return existing;

			var invocationList = del.GetInvocationList();
			if (invocationList.Length > 1)
			{
				// Multicast: rebuild each entry against its cloned target, then recombine in order. The
				// combined delegate is registered so a second reference to this same multicast is shared.
				var clonedEntries = new Delegate[invocationList.Length];
				for (var i = 0; i < invocationList.Length; i++)
					clonedEntries[i] = (Delegate)CloneDelegate(invocationList[i], invocationList[i].GetType(), trackPaths ? $"{path}[{i}]" : null);

				var combined = Delegate.Combine(clonedEntries);
				RegisterClone(del, combined);
				return combined;
			}

			// A static method has no target to clone; the delegate is immutable and safe to share.
			if (del.Target == null)
			{
				RegisterClone(del, del);
				return del;
			}

			var clonedTarget = CloneValue(del.Target, trackPaths ? $"{path}.Target" : null);
			var rebuilt = Delegate.CreateDelegate(type, clonedTarget, del.Method);
			RegisterClone(del, rebuilt);
			return rebuilt;
		}

		object CloneArray(Array array, Type type, string path)
		{
			if (type.GetArrayRank() != 1)
			{
				unsupported.Add($"{path} (rank {type.GetArrayRank()} array)");
				return null;
			}

			var elementType = type.GetElementType();
			var clone = Array.CreateInstance(elementType, array.Length);
			RegisterClone(array, clone);

			if (elementType.IsPrimitive || elementType.IsEnum)
				Array.Copy(array, clone, array.Length);
			else if (!elementType.IsValueType)
			{
				// Reference elements: iterate as object[] so neither GetValue nor SetValue boxes.
				var source = (object[])array;
				var target = (object[])clone;
				for (var i = 0; i < source.Length; i++)
					target[i] = CloneValue(source[i], trackPaths ? $"{path}[{i}]" : null);
			}
			else
				for (var i = 0; i < array.Length; i++)
					clone.SetValue(CloneValue(array.GetValue(i), trackPaths ? $"{path}[{i}]" : null), i);

			return clone;
		}

		object CloneDictionary(IDictionary dictionary, Type type, string path)
		{
			// Rebuilt by re-inserting every entry rather than copied field-wise, because the internal
			// buckets hold hash codes derived from the original keys. For keys compared by reference
			// those hash codes do not survive cloning, and a field-wise copy would be unsearchable.
			// A non-default comparer is carried over (see CreateWithComparer) so lookup semantics - e.g.
			// case-insensitive string keys - survive the clone.
			var clone = (IDictionary)CreateWithComparer(type, dictionary);
			RegisterClone(dictionary, clone);

			foreach (DictionaryEntry entry in dictionary)
				clone.Add(CloneValue(entry.Key, trackPaths ? $"{path}.Key" : null), CloneValue(entry.Value, trackPaths ? $"{path}.Value" : null));

			return clone;
		}

		object CloneHashSet(object set, Type type, string path)
		{
			// Rebuilt for the same reason as a dictionary, comparer included.
			var clone = CreateWithComparer(type, set);
			RegisterClone(set, clone);

			var add = HashSetAddMethod[type];
			var index = 0;
			foreach (var item in (IEnumerable)set)
				add.Invoke(clone, [CloneValue(item, trackPaths ? $"{path}[{index++}]" : null)]);

			return clone;
		}

		/// <summary>
		/// Records that <paramref name="clone"/> is the copy of <paramref name="original"/> in the clone
		/// map, so later references resolve to it, and counts it.
		/// </summary>
		void RegisterClone(object original, object clone)
		{
			clones.Add(original, clone);
			ClonedObjectCount++;
		}

		/// <summary>
		/// Creates an empty instance of a Dictionary or HashSet, preserving the source's comparer when it
		/// exposes one and offers a matching constructor. Comparers are stateless, externally-owned value
		/// objects (e.g. StringComparer.OrdinalIgnoreCase), so the source's comparer is shared rather than
		/// cloned. Falls back to the parameterless constructor for types without a comparer constructor.
		/// </summary>
		static object CreateWithComparer(Type type, object source)
		{
			var comparerProperty = type.GetProperty("Comparer", BindingFlags.Instance | BindingFlags.Public);
			if (comparerProperty != null)
			{
				var comparer = comparerProperty.GetValue(source);
				if (comparer != null)
				{
					var ctor = type.GetConstructor([comparerProperty.PropertyType]);
					if (ctor != null)
						return ctor.Invoke([comparer]);
				}
			}

			return Activator.CreateInstance(type);
		}

		void CloneFieldsInto(object original, object clone, Type type, string path)
		{
			// Fields per type are cached: GetFields allocates and walks metadata on every call, which the
			// per-object path cannot afford, and the result is identical for every instance of a type.
			foreach (var field in FieldsByType[type])
				field.SetValue(clone, CloneValue(field.GetValue(original), trackPaths ? $"{path}.{field.Name}" : null));
		}

		static FieldInfo[] ComputeFields(Type type)
		{
			// DeclaredOnly walking the hierarchy, so that private fields of base classes are included,
			// own fields first - the same order a per-instance walk produced.
			var fields = new List<FieldInfo>();
			for (var t = type; t != null && t != typeof(object); t = t.BaseType)
				fields.AddRange(t.GetFields(FieldFlags));

			return fields.ToArray();
		}

		static MethodInfo ComputeHashSetAddMethod(Type type)
		{
			return type.GetMethod(nameof(HashSet<object>.Add), BindingFlags.Instance | BindingFlags.Public);
		}

		static bool ComputeStructHoldsReferences(Type type)
		{
			foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				var fieldType = field.FieldType;
				if (fieldType.IsPrimitive || fieldType.IsEnum)
					continue;

				// A struct cannot contain itself, so this terminates.
				if (fieldType.IsValueType)
				{
					if (ComputeStructHoldsReferences(fieldType))
						return true;

					continue;
				}

				return true;
			}

			return false;
		}
	}
}
