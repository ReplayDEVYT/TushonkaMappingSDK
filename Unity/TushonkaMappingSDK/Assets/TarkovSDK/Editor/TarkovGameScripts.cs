using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace TarkovSDK.Editor
{
    // A game MonoBehaviour's own fields, read from its serialized bytes by the layout of the SDK's stub of its class
    // (GameStubs have the game's serialized fields, in order): Unity writes the fields it serializes in declaration
    // order, base classes first, primitives under 4 bytes and arrays padded to 4, references as PPtrs (file id + path
    // id). The read is only trusted when it ends exactly at the object's end (Read returns false otherwise, and the
    // component is added without its values). Measure mode (no resolve) only walks the bytes.
    public sealed class GameScriptReader
    {
        // a reference's object, for a field of the given type (null in measure mode: values aren't made)
        public Func<GamePPtr, Type, object> Resolve;
        public bool Create => Resolve != null;
        // why the last read failed
        public string Error;

        private const int MaxDepth = 10;
        private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();

        // reads the script's fields into target (a component of the stub's type), or just walks them
        public bool Read(GameReader r, long size, Type type, object target)
        {
            Error = null;
            long start = r.Position;
            try
            {
                ReadFields(r, type, target, 0);
            }
            catch (Exception e)
            {
                Error = e.Message;
                return false;
            }
            long used = r.Position - start;
            if (used != size)
            {
                Error = $"read {used} of {size} bytes (the stub's fields don't match the game's)";
                return false;
            }
            return true;
        }

        // the fields Unity serializes on the type, base classes first, up to MonoBehaviour
        public static FieldInfo[] Fields(Type type)
        {
            lock (FieldCache)
                if (FieldCache.TryGetValue(type, out var cached))
                    return cached;
            var chain = new List<Type>();
            for (var t = type; t != null && !IsUnityBase(t); t = t.BaseType)
                chain.Insert(0, t);
            var fields = new List<FieldInfo>();
            foreach (var t in chain)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(f => f.MetadataToken))
                    if (Serialized(f))
                        fields.Add(f);
            var result = fields.ToArray();
            lock (FieldCache)
                FieldCache[type] = result;
            return result;
        }

        private static bool IsUnityBase(Type t) =>
            t.FullName == "UnityEngine.MonoBehaviour" || t.FullName == "UnityEngine.Behaviour" || t.FullName == "UnityEngine.Component" ||
            t.FullName == "UnityEngine.Object" || t.FullName == "UnityEngine.ScriptableObject" || t.FullName == "System.Object" || t.FullName == "System.ValueType";

        private static bool HasAttribute(MemberInfo m, string name) => m.GetCustomAttributesData().Any(a => a.AttributeType.FullName == name);

        private static bool Serialized(FieldInfo f)
        {
            if (f.IsStatic || f.IsInitOnly || f.IsLiteral || f.IsNotSerialized)
                return false;
            if (HasAttribute(f, "UnityEngine.SerializeReference"))
                throw new NotSupportedException($"{f.DeclaringType.Name}.{f.Name} is a [SerializeReference] field");
            if (!f.IsPublic && !HasAttribute(f, "UnityEngine.SerializeField"))
                return false;
            return Serializable(f.FieldType, true);
        }

        private static bool IsUnityObject(Type t)
        {
            for (; t != null; t = t.BaseType)
                if (t.FullName == "UnityEngine.Object")
                    return true;
            return false;
        }

        private static readonly Dictionary<string, int> Builtins = new Dictionary<string, int>
        {
            { "UnityEngine.Vector2", 2 }, { "UnityEngine.Vector3", 3 }, { "UnityEngine.Vector4", 4 }, { "UnityEngine.Quaternion", 4 },
            { "UnityEngine.Color", 4 }, { "UnityEngine.Rect", 4 }, { "UnityEngine.Bounds", 6 }, { "UnityEngine.Matrix4x4", 16 },
            { "UnityEngine.Vector2Int", -2 }, { "UnityEngine.Vector3Int", -3 }, { "UnityEngine.RectInt", -4 }, { "UnityEngine.BoundsInt", -6 },
            { "UnityEngine.Color32", 0 }, { "UnityEngine.LayerMask", 0 }, { "UnityEngine.AnimationCurve", 0 }, { "UnityEngine.Gradient", 0 },
            { "UnityEngine.Hash128", 0 },
        };

        private static bool IsPrimitive(Type t) => t.IsPrimitive && t.FullName != "System.IntPtr" && t.FullName != "System.UIntPtr";

        // whether Unity serializes a field of this type (collections: one level, of a serializable element)
        private static bool Serializable(Type t, bool allowCollection)
        {
            if (IsPrimitive(t) || t.FullName == "System.String" || t.IsEnum)
                return true;
            if (IsUnityObject(t) || Builtins.ContainsKey(t.FullName ?? ""))
                return true;
            if (t.IsArray)
                return allowCollection && t.GetArrayRank() == 1 && Serializable(t.GetElementType(), false);
            if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Collections.Generic.List`1")
                return allowCollection && Serializable(t.GetGenericArguments()[0], false);
            if (t.IsInterface || t.IsAbstract || t.IsPointer || t.ContainsGenericParameters)
                return false;
            if (typeof(Delegate).IsAssignableFrom(t) || t.FullName != null && t.FullName.StartsWith("System."))
                return false;
            return t.IsSerializable;
        }

        private void ReadFields(GameReader r, Type type, object target, int depth)
        {
            foreach (var f in Fields(type))
            {
                var value = ReadValue(r, f.FieldType, depth + 1);
                if (Create && target != null)
                {
                    try
                    {
                        if (value != null || !f.FieldType.IsValueType)
                            f.SetValue(target, value);
                    }
                    catch (ArgumentException)
                    {
                        // a reference whose object isn't of the field's type stays empty
                    }
                }
            }
        }

        // inArray: an array's element (small primitives aren't padded one by one there, only the array as a whole)
        private object ReadValue(GameReader r, Type t, int depth, bool inArray = false)
        {
            if (t.IsEnum)
            {
                // enums are written as their underlying type, padded like it
                var underlying = t.GetEnumUnderlyingType();
                var raw = ReadPrimitive(r, underlying, !inArray);
                return Create ? Enum.ToObject(t, raw) : null;
            }
            if (IsPrimitive(t))
            {
                var v = ReadPrimitive(r, t, !inArray);
                return Create ? v : null;
            }
            if (t.FullName == "System.String")
                return r.AlignedString();
            if (IsUnityObject(t))
            {
                var p = r.PPtr();
                return Create && !p.IsNull ? Resolve(p, t) : null;
            }
            if (t.IsArray || t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Collections.Generic.List`1")
            {
                var element = t.IsArray ? t.GetElementType() : t.GetGenericArguments()[0];
                int count = r.I32();
                if (count < 0 || count > 1 << 24)
                    throw new InvalidOperationException($"bad array length {count} for {t.Name}");
                IList list = null;
                if (Create)
                    list = t.IsArray ? (IList)Array.CreateInstance(element, count) : (IList)Activator.CreateInstance(t);
                for (int i = 0; i < count; i++)
                {
                    var v = ReadValue(r, element, depth, true);
                    if (list != null)
                    {
                        if (t.IsArray)
                        {
                            if (v != null)
                                try { list[i] = v; } catch (ArgumentException) { }
                        }
                        else
                            try { list.Add(v ?? Default(element)); } catch (ArgumentException) { list.Add(Default(element)); }
                    }
                }
                r.Align();
                return list;
            }
            if (Builtins.ContainsKey(t.FullName))
                return ReadBuiltin(r, t);
            if (depth > MaxDepth)
                return null;
            // a [Serializable] class or struct, inline
            object instance = null;
            if (Create)
            {
                try
                {
                    instance = Activator.CreateInstance(t, true);
                }
                catch (Exception)
                {
                    instance = FormatterServices.GetUninitializedObject(t);
                }
            }
            ReadFields(r, t, instance, depth);
            return instance;
        }

        private static object Default(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

        private static object ReadPrimitive(GameReader r, Type t, bool align)
        {
            object v;
            switch (t.FullName)
            {
                case "System.Boolean": v = r.U8() != 0; break;
                case "System.Byte": v = r.U8(); break;
                case "System.SByte": v = (sbyte)r.U8(); break;
                case "System.Char": v = (char)r.U16(); break;
                case "System.Int16": v = r.I16(); break;
                case "System.UInt16": v = r.U16(); break;
                case "System.Int32": return r.I32();
                case "System.UInt32": return r.U32();
                case "System.Int64": return r.I64();
                case "System.UInt64": return r.U64();
                case "System.Single": return r.F32();
                case "System.Double": return r.F64();
                default: throw new NotSupportedException("primitive " + t.FullName);
            }
            if (align)
                r.Align();
            return v;
        }

        // Unity's own structs and classes, made through GameScriptValues (UnityEngine types) when creating
        private object ReadBuiltin(GameReader r, Type t)
        {
            int floats = Builtins[t.FullName];
            if (floats > 0)
            {
                var data = new float[floats];
                for (int i = 0; i < floats; i++)
                    data[i] = r.F32();
                return Create ? GameScriptValues.Make?.Invoke(t.FullName, data, null) : null;
            }
            if (floats < 0)
            {
                var ints = new int[-floats];
                for (int i = 0; i < ints.Length; i++)
                    ints[i] = r.I32();
                return Create ? GameScriptValues.Make?.Invoke(t.FullName, null, ints) : null;
            }
            switch (t.FullName)
            {
                case "UnityEngine.Color32":
                case "UnityEngine.LayerMask":
                {
                    int bits = r.I32();
                    return Create ? GameScriptValues.Make?.Invoke(t.FullName, null, new[] { bits }) : null;
                }
                case "UnityEngine.Hash128":
                {
                    var ints = new int[4];
                    for (int i = 0; i < 4; i++)
                        ints[i] = r.I32();
                    return Create ? GameScriptValues.Make?.Invoke(t.FullName, null, ints) : null;
                }
                case "UnityEngine.AnimationCurve":
                {
                    // Keyframe: time, value, inSlope, outSlope, weightedMode (int), inWeight, outWeight
                    int count = r.I32();
                    if (count < 0 || count > 1 << 20)
                        throw new InvalidOperationException("bad curve length " + count);
                    var data = new float[count * 7];
                    for (int i = 0; i < data.Length; i++)
                        data[i] = i % 7 == 4 ? r.I32() : r.F32();
                    var infinity = new[] { r.I32(), r.I32(), r.I32() };
                    return Create ? GameScriptValues.Make?.Invoke(t.FullName, data, infinity) : null;
                }
                case "UnityEngine.Gradient":
                {
                    // 8 RGBA keys, 8 colour times, 8 alpha times (UInt16 of 65535), mode, colour space, key counts
                    var data = new float[32];
                    for (int i = 0; i < 32; i++)
                        data[i] = r.F32();
                    var ints = new int[20];
                    for (int i = 0; i < 16; i++)
                        ints[i] = r.U16();
                    for (int i = 16; i < 20; i++)
                        ints[i] = r.U8();
                    r.Align();
                    return Create ? GameScriptValues.Make?.Invoke(t.FullName, data, ints) : null;
                }
            }
            throw new NotSupportedException(t.FullName);
        }
    }

    // makes Unity's own value types from the raw numbers (set by the Unity side, TarkovGameImport)
    public static class GameScriptValues
    {
        public static Func<string, float[], int[], object> Make;
    }
}
