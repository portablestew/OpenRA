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
	/// <para>Delegates are not cloned. Their captured variables live in compiler-generated closure
	/// objects that may reference anything, so a copied delegate would silently continue to act on the
	/// original graph. They are recorded in <see cref="Unsupported"/> and left null, so that callers
	/// find out rather than inheriting a corrupt copy.</para>
	/// </remarks>
	public sealed class ObjectGraphCloner
	{
		const BindingFlags FieldFlags =
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

		static readonly ConcurrentCache<Type, bool> StructHoldsReferences = new(ComputeStructHoldsReferences);

		readonly Dictionary<object, object> clones = new(ReferenceEqualityComparer.Instance);
		readonly Func<object, bool> shareInstance;
		readonly List<string> unsupported = [];

		/// <summary>
		/// Field paths at which a value could not be cloned and was left null in the copy.
		/// A successful clone leaves this empty.
		/// </summary>
		public IReadOnlyList<string> Unsupported => unsupported;

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
			return (T)CloneValue(root, typeof(T).Name);
		}

		object CloneValue(object value, string path)
		{
			if (value == null)
				return null;

			var type = value.GetType();

			// Immutable, so safe to share.
			if (type.IsPrimitive || type.IsEnum || value is string)
				return value;

			if (value is Delegate)
			{
				unsupported.Add($"{path} ({type.Name} delegate)");
				return null;
			}

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

			if (value is IDictionary dictionary)
				return CloneDictionary(dictionary, type, path);

			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>))
				return CloneHashSet(value, type, path);

			var clone = RuntimeHelpers.GetUninitializedObject(type);
			clones.Add(value, clone);
			CloneFieldsInto(value, clone, type, path);
			return clone;
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
			clones.Add(array, clone);

			if (elementType.IsPrimitive || elementType.IsEnum)
				Array.Copy(array, clone, array.Length);
			else
				for (var i = 0; i < array.Length; i++)
					clone.SetValue(CloneValue(array.GetValue(i), $"{path}[{i}]"), i);

			return clone;
		}

		object CloneDictionary(IDictionary dictionary, Type type, string path)
		{
			// Rebuilt by re-inserting every entry rather than copied field-wise, because the internal
			// buckets hold hash codes derived from the original keys. For keys compared by reference
			// those hash codes do not survive cloning, and a field-wise copy would be unsearchable.
			// Note that a non-default comparer is not carried over.
			var clone = (IDictionary)Activator.CreateInstance(type);
			clones.Add(dictionary, clone);

			foreach (DictionaryEntry entry in dictionary)
				clone.Add(CloneValue(entry.Key, $"{path}.Key"), CloneValue(entry.Value, $"{path}.Value"));

			return clone;
		}

		object CloneHashSet(object set, Type type, string path)
		{
			// Rebuilt for the same reason as a dictionary.
			var clone = Activator.CreateInstance(type);
			clones.Add(set, clone);

			var add = type.GetMethod(nameof(HashSet<object>.Add), BindingFlags.Instance | BindingFlags.Public);
			var index = 0;
			foreach (var item in (IEnumerable)set)
				add.Invoke(clone, [CloneValue(item, $"{path}[{index++}]")]);

			return clone;
		}

		void CloneFieldsInto(object original, object clone, Type type, string path)
		{
			// DeclaredOnly walking the hierarchy, so that private fields of base classes are included.
			for (var t = type; t != null && t != typeof(object); t = t.BaseType)
				foreach (var field in t.GetFields(FieldFlags))
					field.SetValue(clone, CloneValue(field.GetValue(original), $"{path}.{field.Name}"));
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
