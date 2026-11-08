using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Stardate.Core;

/// <summary>
/// Leaves a field out of a saved state: the machine a state is loaded into keeps its own. For
/// what the machine is built with rather than what it does, such as the folders it was given.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class NotSavedAttribute : Attribute
{
}

/// <summary>
/// Saving and restoring a machine whole: every field of every device, found by walking the
/// objects from the machine down, so that nothing a device keeps can be forgotten. Values,
/// structures and strings are written as they are. Objects, arrays and collections are written
/// once each, and referred to by number after that, so the wiring between them survives: two
/// parts sharing an array still share it.
/// </summary>
/// <remarks>
/// <para>
/// Left out: fields marked <see cref="NotSavedAttribute"/>; delegates, which are wiring set up
/// when the machine is built; and the <b>fixed</b> objects, which are written as a reference to
/// the loading machine's own. Those are the objects in the core's static fields (lookup tables
/// and the like, the same in every run) and the ones the machine names, the MT-32's ROMs among
/// them, which are the user's, large, and loaded again by the machine a state goes into.
/// </para>
/// <para>
/// Loading fills in the objects already there, so delegates that point at them still do; only
/// an object the loading machine lacks is made new. An open file is saved as its name and
/// position, and opened again. A state is stamped with a fingerprint of every field's name and
/// type, and a build whose fields differ refuses it rather than misreading it.
/// </para>
/// </remarks>
internal static class MachineState
{
    private static readonly byte[] Magic = "SDSTATE1"u8.ToArray();

    private static readonly Assembly Core = typeof(MachineState).Assembly;

    private static readonly Dictionary<Type, FieldInfo[]> FieldCache = [];

    /// <summary>A fingerprint of the fields of every type in the core.</summary>
    private static readonly Lazy<ulong> Layout = new(ComputeLayout);

    /// <summary>The objects in the core's static fields, in an order that's the same in every run.</summary>
    private static readonly Lazy<object[]> Statics = new(FindStatics);

    // reference kinds, after the object's number when it's new
    private const byte KindObject = 0;
    private const byte KindArray = 1;
    private const byte KindList = 2;
    private const byte KindQueue = 3;
    private const byte KindSet = 4;
    private const byte KindDictionary = 5;
    private const byte KindFile = 6;
    private const byte KindFileInfo = 7;
    private const byte KindDirectoryInfo = 8;
    private const byte KindText = 9;

    /// <summary>Writes a header and then the root and everything it reaches.</summary>
    public static void Save(object root, IReadOnlyList<object> fixedObjects, string header, Stream stream)
    {
        using var compressed = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
        using var writer = new BinaryWriter(compressed);
        writer.Write(Magic);
        writer.Write(Layout.Value);
        writer.Write(header);
        new StateWriter(writer, [.. Statics.Value, .. fixedObjects]).WriteReference(root);
    }

    /// <summary>
    /// Reads a state's header, checks it with the function given (which throws if the machine
    /// can't take the state), then fills in the root from the rest.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is not a state, or is one from a different build.</exception>
    public static void Load(object root, IReadOnlyList<object> fixedObjects, Action<string> checkHeader, Stream stream)
    {
        // read the whole thing first, so a truncated file fails before the machine is touched
        var buffer = new MemoryStream();
        try
        {
            using var compressed = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            compressed.CopyTo(buffer);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("This is not a saved state.");
        }

        buffer.Position = 0;
        using var reader = new BinaryReader(buffer);
        if (buffer.Length < Magic.Length + sizeof(ulong) + 1 || !reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("This is not a saved state.");
        }

        if (reader.ReadUInt64() != Layout.Value)
        {
            throw new InvalidDataException("This state was saved by a different version of Stardate.");
        }

        checkHeader(reader.ReadString());
        try
        {
            new StateReader(reader, [.. Statics.Value, .. fixedObjects]).ReadReference(root);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("This saved state is cut short.");
        }
    }

    /// <summary>The fields saved for a type, its base types' included, in declaration order.</summary>
    private static FieldInfo[] Fields(Type type)
    {
        lock (FieldCache)
        {
            if (!FieldCache.TryGetValue(type, out var fields))
            {
                var all = new List<FieldInfo>();
                for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
                {
                    all.InsertRange(0, t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(f => !f.IsDefined(typeof(NotSavedAttribute)) && !typeof(Delegate).IsAssignableFrom(f.FieldType))
                        .OrderBy(f => f.MetadataToken));
                }

                fields = [.. all];
                FieldCache[type] = fields;
            }

            return fields;
        }
    }

    private static IEnumerable<Type> CoreTypes()
        => Core.GetTypes().Where(t => !t.ContainsGenericParameters).OrderBy(t => t.FullName, StringComparer.Ordinal);

    private static ulong ComputeLayout()
    {
        var text = new StringBuilder();
        foreach (var type in CoreTypes().Where(t => (t.IsClass && !t.IsAbstract) || (t.IsValueType && !t.IsEnum)))
        {
            text.Append(type.FullName).Append('{');
            foreach (var field in Fields(type))
            {
                text.Append(field.FieldType.FullName).Append(' ').Append(field.Name).Append(';');
            }

            text.Append('}');
        }

        return BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static object[] FindStatics()
    {
        var found = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var type in CoreTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !f.IsLiteral && !f.FieldType.IsValueType)
                .OrderBy(f => f.MetadataToken))
            {
                if (field.GetValue(null) is { } value and not string && seen.Add(value))
                {
                    found.Add(value);
                }
            }
        }

        return [.. found];
    }

    private static bool IsStructure(Type type)
        => type.IsValueType && !type.IsPrimitive && !type.IsEnum
            && (type.Assembly == Core || (type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal)));

    private static byte KindOf(Type type)
    {
        if (type.IsArray)
        {
            return KindArray;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>))
            {
                return KindList;
            }

            if (definition == typeof(Queue<>))
            {
                return KindQueue;
            }

            if (definition == typeof(HashSet<>))
            {
                return KindSet;
            }

            if (definition == typeof(Dictionary<,>))
            {
                return KindDictionary;
            }
        }

        if (type == typeof(FileStream))
        {
            return KindFile;
        }

        if (type == typeof(StringBuilder))
        {
            return KindText;
        }

        if (type == typeof(FileInfo))
        {
            return KindFileInfo;
        }

        if (type == typeof(DirectoryInfo))
        {
            return KindDirectoryInfo;
        }

        if (type.IsClass && type.Assembly == Core && !typeof(Delegate).IsAssignableFrom(type))
        {
            return KindObject;
        }

        throw new NotSupportedException($"A saved state can't hold a {type}.");
    }

    // a type a state names: only the kinds it can hold, so a state can't make anything else
    private static Type Resolve(string name)
    {
        var type = Core.GetType(name) ?? Type.GetType(name);
        if (type is null)
        {
            throw new InvalidDataException($"A saved state names an unknown {name}.");
        }

        try
        {
            if (type.IsArray)
            {
                // an array of values, or of things a state can hold, or that a core interface names
                var element = type.GetElementType()!;
                if (!element.IsValueType && element != typeof(string) && !(element.IsInterface && element.Assembly == Core))
                {
                    KindOf(element);
                }
            }
            else
            {
                KindOf(type);
            }
        }
        catch (NotSupportedException)
        {
            throw new InvalidDataException($"A saved state names a {name}, which it can't hold.");
        }

        return type;
    }

    private static string NameOf(Type type) => type.Assembly == Core ? type.FullName! : type.AssemblyQualifiedName!;

    private sealed class StateWriter(BinaryWriter writer, object[] fixedObjects)
    {
        private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, int> _fixed = FixedIndex(fixedObjects);

        private static Dictionary<object, int> FixedIndex(object[] objects)
        {
            var index = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < objects.Length; i++)
            {
                index.TryAdd(objects[i], i);
            }

            return index;
        }

        public void Write(Type type, object? value)
        {
            if (type.IsEnum)
            {
                writer.Write(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (type.IsPrimitive)
            {
                WritePrimitive(value!);
            }
            else if (type == typeof(DateTime))
            {
                // a time, as the CD's directory gives each file
                writer.Write(((DateTime)value!).ToBinary());
            }
            else if (type == typeof(string))
            {
                writer.Write(value is not null);
                if (value is not null)
                {
                    writer.Write((string)value);
                }
            }
            else if (Nullable.GetUnderlyingType(type) is { } underlying)
            {
                writer.Write(value is not null);
                if (value is not null)
                {
                    Write(underlying, value);
                }
            }
            else if (IsStructure(type))
            {
                foreach (var field in Fields(type))
                {
                    Write(field.FieldType, field.GetValue(value));
                }
            }
            else if (!type.IsValueType)
            {
                WriteReference(value);
            }
            else
            {
                throw new NotSupportedException($"A saved state can't hold a {type}.");
            }
        }

        private void WritePrimitive(object value)
        {
            switch (value)
            {
                case bool v: writer.Write(v); break;
                case byte v: writer.Write(v); break;
                case sbyte v: writer.Write(v); break;
                case char v: writer.Write((ushort)v); break;
                case short v: writer.Write(v); break;
                case ushort v: writer.Write(v); break;
                case int v: writer.Write(v); break;
                case uint v: writer.Write(v); break;
                case long v: writer.Write(v); break;
                case ulong v: writer.Write(v); break;
                case float v: writer.Write(v); break;
                case double v: writer.Write(v); break;
                default: throw new NotSupportedException($"A saved state can't hold a {value.GetType()}.");
            }
        }

        // -1 for null; -2 and down for a fixed object; an object seen before by its number; a new
        // one by the next number, its type, and what's in it
        public void WriteReference(object? value)
        {
            if (value is null)
            {
                writer.Write(-1);
                return;
            }

            if (_fixed.TryGetValue(value, out int fixedIndex))
            {
                writer.Write(-2 - fixedIndex);
                return;
            }

            if (_ids.TryGetValue(value, out int id))
            {
                writer.Write(id);
                return;
            }

            var type = value.GetType();
            byte kind = KindOf(type);
            _ids[value] = _ids.Count;
            writer.Write(_ids[value]);
            writer.Write(kind);
            switch (kind)
            {
                case KindObject:
                    writer.Write(NameOf(type));
                    foreach (var field in Fields(type))
                    {
                        Write(field.FieldType, field.GetValue(value));
                    }

                    break;
                case KindArray:
                    writer.Write(NameOf(type));
                    WriteArray((Array)value);
                    break;
                case KindList or KindQueue or KindSet:
                    {
                        writer.Write(NameOf(type));
                        var items = ((IEnumerable)value).Cast<object?>().ToList();
                        var element = type.GetGenericArguments()[0];
                        writer.Write(items.Count);
                        foreach (object? item in items)
                        {
                            Write(element, item);
                        }

                        break;
                    }

                case KindDictionary:
                    {
                        writer.Write(NameOf(type));
                        var arguments = type.GetGenericArguments();
                        var dictionary = (IDictionary)value;
                        writer.Write(dictionary.Count);
                        foreach (DictionaryEntry entry in dictionary)
                        {
                            Write(arguments[0], entry.Key);
                            Write(arguments[1], entry.Value);
                        }

                        break;
                    }

                case KindFile:
                    {
                        // an open file: where it is and how far in, to open it again
                        var file = (FileStream)value;
                        writer.Write(file.Name);
                        writer.Write(file.CanWrite);
                        writer.Write(file.Position);
                        break;
                    }

                case KindFileInfo or KindDirectoryInfo:
                    writer.Write(((FileSystemInfo)value).FullName);
                    break;
                case KindText:
                    writer.Write(value.ToString()!);
                    break;
            }
        }

        private void WriteArray(Array array)
        {
            writer.Write(array.Rank);
            for (int dimension = 0; dimension < array.Rank; dimension++)
            {
                writer.Write(array.GetLength(dimension));
            }

            var element = array.GetType().GetElementType()!;
            if (element.IsPrimitive)
            {
                var bytes = new byte[Buffer.ByteLength(array)];
                Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
                writer.Write(bytes);
                return;
            }

            if (array.Rank != 1)
            {
                throw new NotSupportedException($"A saved state can't hold a {array.GetType()}.");
            }

            foreach (object? item in array)
            {
                Write(element, item);
            }
        }
    }

    private sealed class StateReader(BinaryReader reader, object[] fixedObjects)
    {
        private readonly Dictionary<int, object> _objects = [];

        // the machine's objects already filled in, and the fixed ones, which never are
        private readonly HashSet<object> _claimed = new(fixedObjects, ReferenceEqualityComparer.Instance);

        /// <summary>Reads a value of the type, filling in the one already there where it can.</summary>
        public object? Read(Type type, object? current)
        {
            if (type.IsEnum)
            {
                return Enum.ToObject(type, reader.ReadInt64());
            }

            if (type.IsPrimitive)
            {
                return ReadPrimitive(type);
            }

            if (type == typeof(DateTime))
            {
                return DateTime.FromBinary(reader.ReadInt64());
            }

            if (type == typeof(string))
            {
                return reader.ReadBoolean() ? reader.ReadString() : null;
            }

            if (Nullable.GetUnderlyingType(type) is { } underlying)
            {
                return reader.ReadBoolean() ? Read(underlying, current) : null;
            }

            if (IsStructure(type))
            {
                // a boxed copy, its fields set one by one
                object boxed = current ?? Activator.CreateInstance(type)!;
                foreach (var field in Fields(type))
                {
                    field.SetValue(boxed, Read(field.FieldType, field.GetValue(boxed)));
                }

                return boxed;
            }

            if (!type.IsValueType)
            {
                return ReadReference(current);
            }

            throw new InvalidDataException($"A saved state can't hold a {type}.");
        }

        private object ReadPrimitive(Type type) => Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => reader.ReadBoolean(),
            TypeCode.Byte => reader.ReadByte(),
            TypeCode.SByte => reader.ReadSByte(),
            TypeCode.Char => (char)reader.ReadUInt16(),
            TypeCode.Int16 => reader.ReadInt16(),
            TypeCode.UInt16 => reader.ReadUInt16(),
            TypeCode.Int32 => reader.ReadInt32(),
            TypeCode.UInt32 => reader.ReadUInt32(),
            TypeCode.Int64 => reader.ReadInt64(),
            TypeCode.UInt64 => reader.ReadUInt64(),
            TypeCode.Single => reader.ReadSingle(),
            TypeCode.Double => reader.ReadDouble(),
            _ => throw new InvalidDataException($"A saved state can't hold a {type}."),
        };

        public object? ReadReference(object? current)
        {
            int id = reader.ReadInt32();
            if (id == -1)
            {
                return null;
            }

            if (id < -1)
            {
                int index = -2 - id;
                return index < fixedObjects.Length
                    ? fixedObjects[index]
                    : throw new InvalidDataException("A saved state refers to something this machine doesn't have.");
            }

            if (_objects.TryGetValue(id, out var seen))
            {
                return seen;
            }

            // the object where this one was is filled in, unless it's fixed or has been already:
            // where the machine has run on since the save, the same object can be at two places
            // that hold two in the state, and filling it twice would make them one
            if (current is not null && !_claimed.Add(current))
            {
                current = null;
            }

            byte kind = reader.ReadByte();
            switch (kind)
            {
                case KindObject:
                    {
                        var type = Resolve(reader.ReadString());
                        object value = current is not null && current.GetType() == type ? current : RuntimeHelpers.GetUninitializedObject(type);
                        _objects[id] = value;
                        foreach (var field in Fields(type))
                        {
                            field.SetValue(value, Read(field.FieldType, field.GetValue(value)));
                        }

                        return value;
                    }

                case KindArray:
                    return ReadArray(id, Resolve(reader.ReadString()), current as Array);
                case KindList or KindQueue or KindSet:
                    return ReadSequence(id, kind, Resolve(reader.ReadString()), current);
                case KindDictionary:
                    return ReadDictionary(id, Resolve(reader.ReadString()), current);
                case KindFile:
                    {
                        string name = reader.ReadString();
                        bool writable = reader.ReadBoolean();
                        long position = reader.ReadInt64();
                        FileStream file;
                        try
                        {
                            file = new FileStream(name, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            throw new InvalidDataException($"The saved state had {name} open, and it can't be opened again: {ex.Message}", ex);
                        }

                        file.Position = position;
                        _objects[id] = file;
                        return file;
                    }

                case KindFileInfo:
                    return _objects[id] = new FileInfo(reader.ReadString());
                case KindDirectoryInfo:
                    return _objects[id] = new DirectoryInfo(reader.ReadString());
                case KindText:
                    {
                        var text = current as StringBuilder ?? new StringBuilder();
                        _objects[id] = text;
                        text.Clear().Append(reader.ReadString());
                        return text;
                    }

                default:
                    throw new InvalidDataException("This saved state is damaged.");
            }
        }

        // an array of the same shape is filled in place, so arrays held from outside the state
        // stay the machine's own
        private Array ReadArray(int id, Type type, Array? current)
        {
            int rank = reader.ReadInt32();
            if (rank != type.GetArrayRank())
            {
                throw new InvalidDataException("This saved state is damaged.");
            }

            var lengths = new int[rank];
            for (int dimension = 0; dimension < rank; dimension++)
            {
                lengths[dimension] = reader.ReadInt32();
            }

            bool same = current is not null && current.GetType() == type
                && Enumerable.Range(0, rank).All(dimension => current.GetLength(dimension) == lengths[dimension]);
            var element = type.GetElementType()!;
            var array = same ? current! : Array.CreateInstance(element, lengths);
            _objects[id] = array;
            if (element.IsPrimitive)
            {
                int length = Buffer.ByteLength(array);
                byte[] bytes = reader.ReadBytes(length);
                if (bytes.Length != length)
                {
                    throw new EndOfStreamException();
                }

                Buffer.BlockCopy(bytes, 0, array, 0, length);
                return array;
            }

            for (int i = 0; i < array.Length; i++)
            {
                array.SetValue(Read(element, array.GetValue(i)), i);
            }

            return array;
        }

        // a list, queue or set: the one there emptied and filled again, so it keeps its comparer
        private object ReadSequence(int id, byte kind, Type type, object? current)
        {
            var element = type.GetGenericArguments()[0];
            object collection = current is not null && current.GetType() == type ? current : Activator.CreateInstance(type)!;
            _objects[id] = collection;
            var old = ((IEnumerable)collection).Cast<object?>().ToList();
            type.GetMethod("Clear")!.Invoke(collection, null);
            var add = type.GetMethod(kind == KindQueue ? "Enqueue" : "Add", [element])!;
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                add.Invoke(collection, [Read(element, kind == KindList && i < old.Count ? old[i] : null)]);
            }

            return collection;
        }

        private object ReadDictionary(int id, Type type, object? current)
        {
            var arguments = type.GetGenericArguments();
            var dictionary = (IDictionary)(current is not null && current.GetType() == type ? current : Activator.CreateInstance(type)!);
            _objects[id] = dictionary;
            var old = new Dictionary<object, object?>();
            foreach (DictionaryEntry entry in dictionary)
            {
                old[entry.Key] = entry.Value;
            }

            dictionary.Clear();
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                object key = Read(arguments[0], null) ?? throw new InvalidDataException("This saved state is damaged.");
                dictionary[key] = Read(arguments[1], old.GetValueOrDefault(key));
            }

            return dictionary;
        }
    }
}
