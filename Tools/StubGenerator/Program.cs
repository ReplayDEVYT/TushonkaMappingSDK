using System.Globalization;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MappingSDK.StubGenerator;

// Emits field-only C# stubs of game types for the Unity SDK project.
//
// The game binds a MonoBehaviour in an asset bundle by assembly + namespace + class name, and binds
// each serialized value by field name. A stub therefore only needs the serialized fields (with the
// same names, types and defaults) and nothing else. This tool reads the real Assembly-CSharp.dll and
// writes exactly that, so the stubs stay correct after a game update: re-run it and commit the diff.
//
// Usage: StubGenerator <Assembly-CSharp.dll> <stub-roots.txt> <output dir>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: StubGenerator <Assembly-CSharp.dll> <stub-roots.txt> <output dir>");
            return 1;
        }

        var dll = Path.GetFullPath(args[0]);
        var rootsFile = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);

        var roots = File.ReadAllLines(rootsFile)
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var generator = new Generator(dll);
        foreach (var root in roots)
            generator.AddRoot(root);
        generator.Run();
        generator.Write(output);

        Console.WriteLine($"Wrote {generator.FileCount} file(s), {generator.TypeCount} type(s) to {output}");
        if (generator.Warnings.Count > 0)
            Console.WriteLine($"{generator.Warnings.Count} warning(s), see _StubReport.txt");
        return 0;
    }
}

internal enum Depth
{
    // declaration and base chain only: enough for fields elsewhere to reference the type
    Shallow = 0,
    // declaration plus every serialized field
    Full = 1,
}

internal sealed class Generator
{
    private static readonly HashSet<string> UnityAssemblyPrefixes = new(StringComparer.Ordinal)
    {
        // assemblies the SDK project provides (built-in modules plus packages in Packages/manifest.json)
        "UnityEngine", "Unity.TextMeshPro", "Unity.Timeline", "Unity.Postprocessing.Runtime",
    };

    // Unity structs whose constructors may appear in field initializers
    private static readonly HashSet<string> ConstructibleUnityStructs = new(StringComparer.Ordinal)
    {
        "UnityEngine.Vector2", "UnityEngine.Vector3", "UnityEngine.Vector4", "UnityEngine.Vector2Int",
        "UnityEngine.Vector3Int", "UnityEngine.Quaternion", "UnityEngine.Color", "UnityEngine.Color32",
        "UnityEngine.Rect",
    };

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit",
        "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int",
        "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
        "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    private readonly ModuleDefinition _game;
    private readonly Dictionary<string, Depth> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeDefinition> _defs = new(StringComparer.Ordinal);
    private readonly Queue<TypeDefinition> _work = new();
    private readonly HashSet<string> _roots = new(StringComparer.Ordinal);

    public readonly List<string> Warnings = new();
    public int FileCount { get; private set; }
    public int TypeCount => _types.Count;

    public Generator(string dllPath)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(dllPath)!);
        _game = ModuleDefinition.ReadModule(dllPath, new ReaderParameters { AssemblyResolver = resolver });
    }

    public void AddRoot(string fullName)
    {
        var def = _game.GetType(fullName.Replace('+', '/'))
                  ?? _game.Types.FirstOrDefault(t => t.FullName == fullName);
        if (def == null)
        {
            Warnings.Add($"root '{fullName}' not found in {_game.Name}");
            return;
        }
        _roots.Add(def.FullName);
        Require(def, Depth.Full);
    }

    public void Run()
    {
        while (_work.Count > 0)
        {
            var def = _work.Dequeue();
            var depth = _types[def.FullName];

            if (def.IsNested)
                Require(def.DeclaringType, Depth.Shallow);

            if (def.IsEnum)
                continue;

            var baseRef = EmittedBase(def, out _);
            // base fields are part of this type's serialized layout, so the base needs the same depth
            if (baseRef != null && IsGameType(baseRef))
                Require(baseRef.Resolve(), depth);

            if (depth == Depth.Full)
                foreach (var field in SerializedFields(def))
                    RequireFieldType(field.FieldType);
        }
    }

    private void Require(TypeDefinition def, Depth depth)
    {
        if (_types.TryGetValue(def.FullName, out var existing) && existing >= depth)
            return;
        _types[def.FullName] = depth;
        _defs[def.FullName] = def;
        _work.Enqueue(def);
    }

    private void RequireFieldType(TypeReference type)
    {
        var element = ElementType(type);
        if (element == null || !IsGameType(element))
            return;
        var def = element.Resolve();
        if (def.IsEnum)
            Require(def, Depth.Full);
        else
            Require(def, IsUnityObject(def) ? Depth.Shallow : Depth.Full);
    }

    // ---------------------------------------------------------------- classification

    private bool IsGameType(TypeReference type) =>
        type.Scope is ModuleDefinition m ? m == _game
        : type.Resolve()?.Module == _game;

    private static bool IsUnityAssembly(string assembly) =>
        UnityAssemblyPrefixes.Any(p => assembly == p || assembly.StartsWith(p + ".", StringComparison.Ordinal));

    private static string AssemblyOf(TypeReference type) =>
        type.Resolve()?.Module.Assembly.Name.Name ?? "";

    private static bool IsUnityType(TypeReference type) => IsUnityAssembly(AssemblyOf(type));

    private static bool IsUnityObject(TypeDefinition? def)
    {
        while (def != null)
        {
            if (def.FullName == "UnityEngine.Object")
                return true;
            def = def.BaseType?.Resolve();
        }
        return false;
    }

    private static bool IsPrimitive(TypeReference type) => type.MetadataType switch
    {
        MetadataType.Boolean or MetadataType.Byte or MetadataType.SByte or MetadataType.Int16 or MetadataType.UInt16
            or MetadataType.Int32 or MetadataType.UInt32 or MetadataType.Int64 or MetadataType.UInt64
            or MetadataType.Single or MetadataType.Double or MetadataType.Char or MetadataType.String => true,
        _ => false,
    };

    // unwraps T[] and List<T>; returns null for shapes Unity does not serialize
    private static TypeReference? ElementType(TypeReference type)
    {
        if (type is ArrayType array)
            return array.Rank == 1 && !(array.ElementType is ArrayType) && !IsList(array.ElementType) ? array.ElementType : null;
        if (IsList(type))
        {
            var arg = ((GenericInstanceType)type).GenericArguments[0];
            return arg is ArrayType || IsList(arg) ? null : arg;
        }
        return type;
    }

    private static bool IsList(TypeReference type) =>
        type is GenericInstanceType g && g.ElementType.FullName == "System.Collections.Generic.List`1";

    // returns null if Unity serializes the type; otherwise the reason it doesn't (or we can't emit it)
    private string? WhyNotSerializable(TypeReference type)
    {
        var element = ElementType(type);
        if (element == null)
            return "nested collection";
        if (element is GenericParameter)
            return null;
        if (IsPrimitive(element))
            return null;
        if (element is GenericInstanceType)
            return "generic type " + element.FullName;

        var def = element.Resolve();
        if (def == null)
            return "unresolvable type " + element.FullName;
        if (def.HasGenericParameters)
            return "generic type " + element.FullName;

        if (IsUnityType(element))
            return null;
        if (!IsGameType(element))
            return IsUnityObject(def) || def.IsEnum || def.IsSerializable
                ? $"type from {AssemblyOf(element)} ({element.FullName})"
                : "not serializable";

        if (def.IsEnum || IsUnityObject(def))
            return null;
        if (def.IsInterface)
            return "interface";
        if (def.IsAbstract)
            return "abstract";
        if (!def.IsSerializable)
            return "not [Serializable]";
        if (!IsEmittableName(def))
            return "obfuscated type name";
        return null;
    }

    private static bool IsCompilerGenerated(ICustomAttributeProvider p) =>
        p.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static bool IsEmittableName(TypeDefinition def)
    {
        for (var t = def; t != null; t = t.DeclaringType)
        {
            var name = StripArity(t.Name);
            if (!IsIdentifier(name) || name.StartsWith("CG_", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string StripArity(string name)
    {
        var i = name.IndexOf('`');
        return i < 0 ? name : name.Substring(0, i);
    }

    private static bool HasAttribute(ICustomAttributeProvider p, string fullName) =>
        p.CustomAttributes.Any(a => a.AttributeType.FullName == fullName);

    // Unity's own rule set: instance, not readonly/const, not [NonSerialized], public or [SerializeField]
    private IEnumerable<FieldDefinition> SerializedFields(TypeDefinition def)
    {
        foreach (var field in def.Fields)
        {
            if (field.IsStatic || field.IsLiteral || field.IsInitOnly || field.IsNotSerialized)
                continue;
            if (HasAttribute(field, "UnityEngine.SerializeReference"))
            {
                Warnings.Add($"{def.FullName}.{field.Name}: [SerializeReference] fields are not emitted");
                continue;
            }
            bool serializeField = HasAttribute(field, "UnityEngine.SerializeField");
            if (!field.IsPublic && !serializeField)
                continue;
            if (!IsIdentifier(field.Name) && BackingFieldProperty(field) == null)
                continue;
            if (field.FieldType.IsPointer || field.FieldType.IsByReference || field.FieldType.IsFunctionPointer)
                continue;

            if (IsNeverSerialized(field.FieldType))
                continue;
            var why = WhyNotSerializable(field.FieldType);
            if (why != null)
            {
                // public fields of non-serializable types are common and harmless; only report
                // ones the author clearly meant to serialize
                if (serializeField || why.StartsWith("type from", StringComparison.Ordinal) || why.StartsWith("generic", StringComparison.Ordinal))
                    Warnings.Add($"{def.FullName}.{field.Name}: skipped ({why})");
                continue;
            }
            yield return field;
        }
    }

    // shapes Unity never serializes, so they are skipped without a warning
    private static bool IsNeverSerialized(TypeReference type)
    {
        var element = ElementType(type) ?? type;
        if (element is GenericInstanceType git && git.ElementType.FullName == "System.Nullable`1")
            return true;
        var def = element.Resolve();
        for (var t = def; t != null; t = t.BaseType?.Resolve())
            if (t.FullName == "System.Delegate")
                return true;
        return false;
    }

    private static string? BackingFieldProperty(FieldDefinition field)
    {
        var name = field.Name;
        if (name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
            return name.Substring(1, name.IndexOf('>') - 1);
        return null;
    }

    // the base we will write in the stub; bases we cannot emit (generic, other assemblies) are
    // skipped and their serialized fields are inlined into this type instead
    private TypeReference? EmittedBase(TypeDefinition def, out List<(FieldDefinition Field, TypeReference Type)> inlined)
    {
        inlined = new List<(FieldDefinition, TypeReference)>();
        if (!def.IsClass || def.IsValueType || def.IsInterface)
            return null;

        var current = def.BaseType;
        while (current != null && current.FullName != "System.Object")
        {
            var currentDef = current.Resolve();
            if (currentDef == null)
                return null;
            bool emittable = (IsUnityType(current) && current is not GenericInstanceType)
                             || (IsGameType(current) && current is not GenericInstanceType && IsEmittableName(currentDef));
            if (emittable)
                return current;

            foreach (var field in SerializedFields(currentDef))
                inlined.Add((field, Substitute(field.FieldType, current)));
            current = currentDef.BaseType is { } b ? Substitute(b, current) : null;
        }
        return null;
    }

    private static TypeReference Substitute(TypeReference type, TypeReference context)
    {
        if (context is not GenericInstanceType git)
            return type;
        if (type is GenericParameter gp && gp.Owner is TypeReference)
            return git.GenericArguments[gp.Position];
        if (type is ArrayType at)
            return new ArrayType(Substitute(at.ElementType, context));
        if (type is GenericInstanceType inner)
        {
            var copy = new GenericInstanceType(inner.ElementType);
            foreach (var a in inner.GenericArguments)
                copy.GenericArguments.Add(Substitute(a, context));
            return copy;
        }
        return type;
    }

    // ---------------------------------------------------------------- emission

    public void Write(string output)
    {
        if (Directory.Exists(output))
            foreach (var file in Directory.GetFiles(output, "*.cs", SearchOption.AllDirectories))
                File.Delete(file);
        Directory.CreateDirectory(output);

        var topLevel = _types.Keys
            .Select(k => _defs[k])
            .Select(TopLevel)
            .DistinctBy(t => t.FullName)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        var report = new StringBuilder();
        report.AppendLine("Generated by Tools/StubGenerator from " + _game.FileName);
        report.AppendLine();
        report.AppendLine("Types:");

        foreach (var top in topLevel)
        {
            if (!IsEmittableName(top))
            {
                Warnings.Add($"{top.FullName}: obfuscated or compiler-generated name, not emitted");
                continue;
            }
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("// Field-only stub of a game type, generated by Tools/StubGenerator.");
            sb.AppendLine("// Do not edit by hand: change Tools/StubGenerator/stub-roots.txt and regenerate.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#pragma warning disable 0649, 0169, 0414");
            sb.AppendLine();

            var indent = "";
            if (!string.IsNullOrEmpty(top.Namespace))
            {
                sb.AppendLine("namespace " + top.Namespace);
                sb.AppendLine("{");
                indent = "    ";
            }
            EmitType(sb, top, indent, report);
            if (!string.IsNullOrEmpty(top.Namespace))
                sb.AppendLine("}");

            var folder = string.IsNullOrEmpty(top.Namespace) ? "_Global" : top.Namespace;
            var dir = Path.Combine(output, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, StripArity(top.Name) + ".cs"), sb.ToString());
            FileCount++;
        }

        report.AppendLine();
        report.AppendLine("Warnings:");
        foreach (var w in Warnings.Distinct().OrderBy(w => w, StringComparer.Ordinal))
            report.AppendLine("  " + w);
        File.WriteAllText(Path.Combine(output, "_StubReport.txt"), report.ToString());
    }

    private static TypeDefinition TopLevel(TypeDefinition def)
    {
        while (def.DeclaringType != null)
            def = def.DeclaringType;
        return def;
    }

    private void EmitType(StringBuilder sb, TypeDefinition def, string indent, StringBuilder report)
    {
        var depth = _types.TryGetValue(def.FullName, out var d) ? d : Depth.Shallow;
        report.AppendLine($"  {(depth == Depth.Full ? "full   " : "shallow")} {def.FullName}");

        var name = StripArity(def.Name);
        if (def.IsEnum)
        {
            EmitEnum(sb, def, name, indent);
            return;
        }

        bool isStruct = def.IsValueType;
        bool isStatic = def.IsAbstract && def.IsSealed && !isStruct;
        bool unityObject = IsUnityObject(def);

        if (!unityObject && !isStatic && def.IsSerializable)
            sb.AppendLine(indent + "[global::System.Serializable]");

        var baseRef = isStruct || isStatic ? null : EmittedBase(def, out _);
        var inlined = new List<(FieldDefinition Field, TypeReference Type)>();
        if (!isStruct && !isStatic && depth == Depth.Full)
            EmittedBase(def, out inlined);

        string modifiers = isStatic ? "public static class" : isStruct ? "public struct"
            : def.IsAbstract ? "public abstract class" : "public class";
        string baseClause = "";
        if (baseRef != null && (unityObject || IsGameType(baseRef)))
            baseClause = " : " + TypeName(baseRef);

        sb.AppendLine($"{indent}{modifiers} {name}{baseClause}");
        sb.AppendLine(indent + "{");
        var inner = indent + "    ";

        bool first = true;
        if (depth == Depth.Full && !isStatic)
        {
            var defaults = isStruct ? new Dictionary<string, string>() : ExtractDefaults(def);
            foreach (var (field, type) in inlined)
            {
                EmitField(sb, field, type, inner, null, isStruct);
                first = false;
            }
            foreach (var field in SerializedFields(def))
            {
                defaults.TryGetValue(field.Name, out var value);
                EmitField(sb, field, field.FieldType, inner, value, isStruct);
                first = false;
            }
        }

        foreach (var nested in def.NestedTypes.OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            if (!_types.ContainsKey(nested.FullName))
                continue;
            if (!IsEmittableName(nested))
            {
                Warnings.Add($"{nested.FullName}: obfuscated or compiler-generated name, not emitted");
                continue;
            }
            if (!first)
                sb.AppendLine();
            EmitType(sb, nested, inner, report);
            first = false;
        }
        sb.AppendLine(indent + "}");
    }

    private void EmitEnum(StringBuilder sb, TypeDefinition def, string name, string indent)
    {
        var underlying = def.Fields.First(f => f.Name == "value__").FieldType;
        if (HasAttribute(def, "System.FlagsAttribute"))
            sb.AppendLine(indent + "[global::System.Flags]");
        var underlyingName = underlying.MetadataType == MetadataType.Int32 ? "" : " : " + TypeName(underlying);
        sb.AppendLine($"{indent}public enum {name}{underlyingName}");
        sb.AppendLine(indent + "{");
        foreach (var f in def.Fields.Where(f => f.IsStatic && f.HasConstant))
        {
            if (!IsIdentifier(f.Name))
            {
                Warnings.Add($"{def.FullName}.{f.Name}: enum member with invalid name skipped");
                continue;
            }
            sb.AppendLine($"{indent}    {Escape(f.Name)} = {Convert.ToString(f.Constant, CultureInfo.InvariantCulture)},");
        }
        sb.AppendLine(indent + "}");
    }

    private void EmitField(StringBuilder sb, FieldDefinition field, TypeReference type, string indent, string? value, bool isStruct)
    {
        foreach (var attr in field.CustomAttributes)
        {
            var text = AttributeText(attr);
            if (text == null)
                continue;
            var property = BackingFieldProperty(field);
            sb.AppendLine($"{indent}[{(property != null ? "field: " : "")}{text}]");
        }

        string access = field.IsPublic ? "public" : field.IsFamily ? "protected" : field.IsAssembly ? "internal" : "private";
        string init = value != null && !isStruct ? " = " + value : "";

        var prop = BackingFieldProperty(field);
        if (prop != null)
        {
            // [field: SerializeField] auto-property; the backing field name is what Unity binds to
            sb.AppendLine($"{indent}public {TypeName(type)} {Escape(prop)} {{ get; set; }}{(init.Length > 0 ? init + ";" : "")}");
            return;
        }
        sb.AppendLine($"{indent}{access} {TypeName(type)} {Escape(field.Name)}{init};");
    }

    // Unity inspector/serialization attributes only; everything else (Odin, BSG tooling) is dropped
    private static string? AttributeText(CustomAttribute attr)
    {
        var type = attr.AttributeType;
        if (type.Namespace != "UnityEngine" && type.Namespace != "UnityEngine.Serialization")
            return null;
        if (!IsUnityAssembly(AssemblyOf(type)))
            return null;
        if (type.Name is "SerializeReference" or "ContextMenuItemAttribute")
            return null;

        var args = new List<string>();
        foreach (var arg in attr.ConstructorArguments)
        {
            var literal = Literal(arg.Value, arg.Type);
            if (literal == null)
                return null;
            args.Add(literal);
        }
        foreach (var named in attr.Properties.Concat(attr.Fields))
        {
            var literal = Literal(named.Argument.Value, named.Argument.Type);
            if (literal == null)
                return null;
            args.Add($"{named.Name} = {literal}");
        }

        var name = "global::" + type.FullName;
        if (name.EndsWith("Attribute", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - "Attribute".Length);
        return args.Count == 0 ? name : $"{name}({string.Join(", ", args)})";
    }

    private static string? Literal(object? value, TypeReference type)
    {
        if (value == null)
            return type.MetadataType == MetadataType.String ? "null" : null;
        var def = type.Resolve();
        if (def != null && def.IsEnum)
            return $"({TypeNameStatic(type)}){Convert.ToString(value, CultureInfo.InvariantCulture)}";
        return value switch
        {
            string s => StringLiteral(s),
            bool b => b ? "true" : "false",
            float f => FloatLiteral(f),
            double dd => double.IsNaN(dd) ? "double.NaN" : double.IsPositiveInfinity(dd) ? "double.PositiveInfinity"
                : double.IsNegativeInfinity(dd) ? "double.NegativeInfinity" : dd.ToString("R", CultureInfo.InvariantCulture) + "d",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture) + "L",
            uint u => u.ToString(CultureInfo.InvariantCulture) + "u",
            ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "ul",
            short sh => $"(short){sh}",
            ushort us => $"(ushort){us}",
            byte by => $"(byte){by}",
            sbyte sb => $"(sbyte){sb}",
            char c => $"(char){(int)c}",
            _ => null,
        };
    }

    private static string FloatLiteral(float f)
    {
        if (float.IsNaN(f)) return "float.NaN";
        if (float.IsPositiveInfinity(f)) return "float.PositiveInfinity";
        if (float.IsNegativeInfinity(f)) return "float.NegativeInfinity";
        if (f == float.MaxValue) return "float.MaxValue";
        if (f == float.MinValue) return "float.MinValue";
        return f.ToString("R", CultureInfo.InvariantCulture) + "f";
    }

    private static string StringLiteral(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < 32 => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    private static string Escape(string name) => CSharpKeywords.Contains(name) ? "@" + name : name;

    private string TypeName(TypeReference type) => TypeNameStatic(type);

    private static string TypeNameStatic(TypeReference type)
    {
        switch (type.MetadataType)
        {
            case MetadataType.Boolean: return "bool";
            case MetadataType.Byte: return "byte";
            case MetadataType.SByte: return "sbyte";
            case MetadataType.Int16: return "short";
            case MetadataType.UInt16: return "ushort";
            case MetadataType.Int32: return "int";
            case MetadataType.UInt32: return "uint";
            case MetadataType.Int64: return "long";
            case MetadataType.UInt64: return "ulong";
            case MetadataType.Single: return "float";
            case MetadataType.Double: return "double";
            case MetadataType.Char: return "char";
            case MetadataType.String: return "string";
            case MetadataType.Object: return "object";
        }
        if (type is GenericParameter gp)
            return gp.Name;
        if (type is ArrayType array)
            return TypeNameStatic(array.ElementType) + "[]";
        if (type is GenericInstanceType git)
        {
            var baseName = TypeNameStatic(git.ElementType);
            return $"{baseName}<{string.Join(", ", git.GenericArguments.Select(TypeNameStatic))}>";
        }

        var parts = new List<string>();
        var t = type;
        while (t.DeclaringType != null)
        {
            parts.Insert(0, StripArity(t.Name));
            t = t.DeclaringType;
        }
        parts.Insert(0, StripArity(t.Name));
        var ns = t.Namespace;
        return "global::" + (string.IsNullOrEmpty(ns) ? "" : ns + ".") + string.Join(".", parts);
    }

    // ---------------------------------------------------------------- field initializer recovery

    private sealed record Expr(string? Code, object? Raw, bool IsThis = false);

    // Field initializers compile to `ldarg.0; <value>; stfld` at the top of every constructor,
    // before the base constructor call. Replay that prefix for simple constant expressions.
    private Dictionary<string, string> ExtractDefaults(TypeDefinition def)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var ctor = def.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && !m.HasParameters)
                   ?? def.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic);
        if (ctor?.Body == null)
            return result;

        var serialized = SerializedFields(def).ToDictionary(f => f.Name, StringComparer.Ordinal);
        var stack = new List<Expr?>();

        foreach (var ins in ctor.Body.Instructions)
        {
            var op = ins.OpCode.Code;
            switch (op)
            {
                case Code.Nop:
                    break;
                case Code.Ldarg_0:
                    stack.Clear();
                    stack.Add(new Expr("this", null, IsThis: true));
                    break;
                case Code.Ldc_I4_M1: Push(stack, -1); break;
                case Code.Ldc_I4_0: Push(stack, 0); break;
                case Code.Ldc_I4_1: Push(stack, 1); break;
                case Code.Ldc_I4_2: Push(stack, 2); break;
                case Code.Ldc_I4_3: Push(stack, 3); break;
                case Code.Ldc_I4_4: Push(stack, 4); break;
                case Code.Ldc_I4_5: Push(stack, 5); break;
                case Code.Ldc_I4_6: Push(stack, 6); break;
                case Code.Ldc_I4_7: Push(stack, 7); break;
                case Code.Ldc_I4_8: Push(stack, 8); break;
                case Code.Ldc_I4_S: Push(stack, (int)(sbyte)ins.Operand); break;
                case Code.Ldc_I4: Push(stack, (int)ins.Operand); break;
                case Code.Ldc_I8: Push(stack, (long)ins.Operand); break;
                case Code.Ldc_R4: Push(stack, (float)ins.Operand); break;
                case Code.Ldc_R8: Push(stack, (double)ins.Operand); break;
                case Code.Ldstr: stack.Add(new Expr(StringLiteral((string)ins.Operand), ins.Operand)); break;
                case Code.Conv_R4:
                    if (stack.Count > 0 && stack[^1]?.Raw is int ci)
                        stack[^1] = new Expr(null, (float)ci);
                    else if (stack.Count > 0)
                        stack[^1] = null;
                    break;
                case Code.Newarr:
                {
                    var count = Pop(stack);
                    stack.Add(count?.Raw is int n
                        ? new Expr($"new {TypeName((TypeReference)ins.Operand)}[{n}]", null)
                        : null);
                    break;
                }
                case Code.Newobj:
                {
                    var method = (MethodReference)ins.Operand;
                    var args = new List<Expr?>();
                    for (int i = 0; i < method.Parameters.Count; i++)
                        args.Insert(0, Pop(stack));
                    stack.Add(NewObject(method, args));
                    break;
                }
                case Code.Stfld:
                {
                    var value = Pop(stack);
                    var target = Pop(stack);
                    var field = ((FieldReference)ins.Operand).Resolve();
                    if (target?.IsThis == true && value != null && field != null && field.DeclaringType == def
                        && serialized.TryGetValue(field.Name, out var f))
                    {
                        var code = ValueFor(value, f.FieldType);
                        if (code != null)
                            result[f.Name] = code;
                    }
                    stack.Clear();
                    break;
                }
                case Code.Call:
                    // the base constructor call ends the initializer block
                    if (ins.Operand is MethodReference { Name: ".ctor" })
                        return result;
                    stack.Clear();
                    stack.Add(null);
                    break;
                default:
                    // anything we can't model: forget the stack until the next `ldarg.0`
                    stack.Clear();
                    stack.Add(null);
                    break;
            }
        }
        return result;

        static void Push(List<Expr?> s, object v) => s.Add(new Expr(null, v));
        static Expr? Pop(List<Expr?> s)
        {
            if (s.Count == 0) return null;
            var e = s[^1];
            s.RemoveAt(s.Count - 1);
            return e;
        }
    }

    private Expr? NewObject(MethodReference ctor, List<Expr?> args)
    {
        var type = ctor.DeclaringType;
        if (IsList(type) && args.Count == 0)
            return new Expr($"new {TypeName(type)}()", null);

        if (!ConstructibleUnityStructs.Contains(type.FullName))
            return null;
        var parts = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == null)
                return null;
            var code = ValueFor(args[i]!, ctor.Parameters[i].ParameterType);
            if (code == null)
                return null;
            parts.Add(code);
        }
        return new Expr($"new {TypeName(type)}({string.Join(", ", parts)})", null);
    }

    private string? ValueFor(Expr value, TypeReference fieldType)
    {
        if (value.Code != null && value.Raw == null)
            return value.Code;
        var raw = value.Raw;
        if (raw == null)
            return value.Code;

        var def = fieldType.Resolve();
        if (def != null && def.IsEnum)
        {
            var number = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            if (number == 0)
                return null;
            var member = def.Fields.FirstOrDefault(f => f.IsStatic && f.HasConstant
                && Convert.ToInt64(f.Constant, CultureInfo.InvariantCulture) == number && IsIdentifier(f.Name));
            return member != null
                ? $"{TypeName(fieldType)}.{Escape(member.Name)}"
                : $"({TypeName(fieldType)})({number})";
        }

        switch (fieldType.MetadataType)
        {
            case MetadataType.Boolean:
                return raw is int bi && bi != 0 ? "true" : null;
            case MetadataType.Single:
                var f = Convert.ToSingle(raw, CultureInfo.InvariantCulture);
                return f == 0f ? null : FloatLiteral(f);
            case MetadataType.Double:
                var d = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return d == 0d ? null : Literal(d, fieldType);
            case MetadataType.String:
                return value.Code;
            case MetadataType.Int32:
                var i = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return i == 0 ? null : i.ToString(CultureInfo.InvariantCulture);
            case MetadataType.Int64:
            case MetadataType.UInt32:
            case MetadataType.UInt64:
            case MetadataType.Int16:
            case MetadataType.UInt16:
            case MetadataType.Byte:
            case MetadataType.SByte:
                var l = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                return l == 0 ? null : $"({TypeName(fieldType)}){l}";
            case MetadataType.Char:
                var c = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return c == 0 ? null : $"(char){c}";
        }
        return null;
    }
}
