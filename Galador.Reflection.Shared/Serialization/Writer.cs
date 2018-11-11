﻿using Galador.Reflection.Serialization.IO;
using Galador.Reflection.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using SRS = System.Runtime.Serialization;

namespace Galador.Reflection.Serialization
{
    public class Writer : Context, IDisposable
    {
        readonly IPrimitiveWriter output;

        public Writer(IPrimitiveWriter output)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            output.WriteVInt(VERSION);
        }

        // v2, whole new Serializer classes
        const ulong VERSION = 0x01_02;

        public void Dispose()
        {
            output.Dispose();
        }

        public SerializationSettings Settings
        {
            get
            {
                if (settings == null)
                    settings = new SerializationSettings();
                return settings;
            }
            set { settings = value; }
        }
        SerializationSettings settings;

        public void Write(object o)
        {
            if (writeRecurseDepth++ == 0)
            {
                var sFlags = Settings.ToFlags();
                output.WriteVInt(sFlags);
            }
            try { Write(RObject, o); }
            finally { writeRecurseDepth--; }
        }
        int writeRecurseDepth = 0;

        [Conditional("DEBUG")]
        internal void DebugInfo(string s)
        {
#if DEBUG
            (output as IO.TokenPrimitiveWriter)?.DebugInfo(s);
#endif
        }

        internal void Write(RuntimeType expected, object value)
        {
            value = AsTypeData(value);

            // write id, continue if first time
            if (expected.IsReference)
            {
                DebugInfo("Reference ID");
                if (TryGetId(value, out var id))
                {
                    output.WriteVInt(id);
                    return;
                }
                id = NewId();
                Register(id, value);
                output.WriteVInt(id);
            }

            // write class info if needed
            var actual = expected;
            if (expected.IsReference && !expected.IsSealed)
            {
                actual = RuntimeType.GetType(value);
                DebugInfo("Actual Class " + actual.FullName);
                Write(RType, actual);
            }

            // only proceed further if value is supported
            if (!expected.IsSupported || !actual.IsSupported)
                return;

            // dispatch to the appropriate write method
            if (actual.Surrogate != null)
            {
                DebugInfo("WriteSurrogate");
                Write(RObject, actual.Surrogate.Convert(value));
            }
            else if (actual.Converter != null && !settings.IgnoreTypeConverter)
            {
                DebugInfo("WriteConverter");
                WriteConverter(actual, value);
            }
            else if (actual.IsISerializable && !Settings.IgnoreISerializable)
            {
                DebugInfo("WriteSerializable");
                WriteISerializable(value);
            }
            else
            {
                DebugInfo("Write+" + actual.Kind);
                switch (actual.Kind)
                {
                    default:
                    case PrimitiveType.None:
                        throw new InvalidOperationException("shouldn't be there");
                    case PrimitiveType.Object:
                        if (actual.IsArray)
                        {
                            WriteArray(actual, value);
                        }
                        else if (actual.IsNullable)
                        {
                            Write(actual.GenericParameters[0], value);
                        }
                        else if (actual.IsEnum)
                        {
                            Write(actual.Element, value);
                        }
                        else
                        {
                            WriteObject(actual, value);
                        }
                        break;
                    case PrimitiveType.Type:
                        ((TypeData)value).Write(this, output);
                        break;
                    case PrimitiveType.String:
                        output.Write((string)value);
                        break;
                    case PrimitiveType.Bytes:
                        output.Write((byte[])value);
                        break;
                    case PrimitiveType.Guid:
                        output.Write((Guid)value);
                        break;
                    case PrimitiveType.Bool:
                        output.Write((bool)value);
                        break;
                    case PrimitiveType.Char:
                        output.Write((char)value);
                        break;
                    case PrimitiveType.Byte:
                        output.Write((byte)value);
                        break;
                    case PrimitiveType.SByte:
                        output.Write((sbyte)value);
                        break;
                    case PrimitiveType.Int16:
                        output.Write((short)value);
                        break;
                    case PrimitiveType.UInt16:
                        output.Write((ushort)value);
                        break;
                    case PrimitiveType.Int32:
                        output.Write((int)value);
                        break;
                    case PrimitiveType.UInt32:
                        output.Write((uint)value);
                        break;
                    case PrimitiveType.Int64:
                        output.Write((long)value);
                        break;
                    case PrimitiveType.UInt64:
                        output.Write((ulong)value);
                        break;
                    case PrimitiveType.Single:
                        output.Write((float)value);
                        break;
                    case PrimitiveType.Double:
                        output.Write((double)value);
                        break;
                    case PrimitiveType.Decimal:
                        output.Write((decimal)value);
                        break;
                }
            }
        }

        void WriteArray(RuntimeType type, object value)
        {
            var array = (Array)value;
            if (array.Rank != type.ArrayRank)
                throw new ArgumentException($"{array} Rank is {array.Rank} instead of expected {type.ArrayRank}");
            for (int i = 0; i < type.ArrayRank; i++)
                output.WriteVInt(array.GetLength(i));
            foreach (var item in array)
                Write(type.Element, item);
        }

        void WriteISerializable(object value)
        {
            var serial = (SRS.ISerializable)value;
            var info = new SRS.SerializationInfo(typeof(object), new SRS.FormatterConverter());
            var ctx = new SRS.StreamingContext(SRS.StreamingContextStates.Persistence);
            serial.GetObjectData(info, ctx);
            output.WriteVInt(info.MemberCount);
            foreach (var item in info)
            {
                Write(RString, item.Name);
                Write(RObject, item.Value);
            }
        }

        void WriteConverter(RuntimeType type, object value)
        {
            var s = type.Converter.ConvertToInvariantString(value);
            Write(RString, s);
        }

        void WriteObject(RuntimeType type, object value)
        {
            foreach (var m in type.RuntimeMembers)
            {
                DebugInfo("Member." + m.Name);
                var p = m.RuntimeMember.GetValue(value);
                Write(m.Type, p);
            }

            switch (type.CollectionType)
            {
                case RuntimeCollectionType.IDictionaryKV:
                    DebugInfo(type.CollectionType.ToString());
                    if (type.writeDictKV == null)
                        type.writeDictKV = FastMethod.GetMethod(GetType().TryGetMethods(nameof(WriteDictionary), new[] { type.Collection1.Type, type.Collection2.Type }, type.Type).First());
                    type.writeDictKV.Invoke(this, value);
                    break;
                case RuntimeCollectionType.ICollectionT:
                    DebugInfo(type.CollectionType.ToString());
                    if (type.writeColT == null)
                        type.writeColT = FastMethod.GetMethod(GetType().TryGetMethods(nameof(WriteCollection), new[] { type.Collection1.Type }, type.Type).First());
                    type.writeColT.Invoke(this, value);
                    break;
                case RuntimeCollectionType.IList:
                    DebugInfo(type.CollectionType.ToString());
                    WriteList((IList)value);
                    break;
                case RuntimeCollectionType.IDictionary:
                    DebugInfo(type.CollectionType.ToString());
                    WriteDict((IDictionary)value);
                    break;
            }
        }

        void WriteList(IList value)
        {
            output.Write(value.IsReadOnly);
            if (value.IsReadOnly)
                return;
            var count = value.Count;
            output.WriteVInt(count);
            for (int i = 0; i < count; i++)
            {
                Write(RObject, value[i]);
            }
        }

        void WriteDict(IDictionary value)
        {
            output.Write(value.IsReadOnly);
            if (value.IsReadOnly)
                return;
            var count = value.Count;
            output.WriteVInt(count);
            foreach (DictionaryEntry kv in value)
            {
                count--;
                Write(kv.Key);
                Write(kv.Value);
            }
            if (count != 0)
                throw new ArgumentException($"({value}.Count reported an incorrect value ({value.Count})");
        }

        void WriteCollection<T>(ICollection<T> value)
        {
            output.Write(value.IsReadOnly);
            if (value.IsReadOnly)
                return;
            var count = value.Count;
            output.WriteVInt(count);
            var surt = RuntimeType.GetType(typeof(T));
            foreach (var item in value)
            {
                count--;
                Write(surt, item);
            }
            if (count != 0)
                throw new ArgumentException($"({value}.Count reported an incorrect value ({value.Count})");
        }

        void WriteDictionary<K, V>(IDictionary<K, V> value)
        {
            output.Write(value.IsReadOnly);
            if (value.IsReadOnly)
                return;
            var count = value.Count;
            output.WriteVInt(count);
            var surk = RuntimeType.GetType(typeof(K));
            var surv = RuntimeType.GetType(typeof(V));
            foreach (var kv in value)
            {
                count--;
                Write(surk, kv.Key);
                Write(surv, kv.Value);
            }
            if (count != 0)
                throw new ArgumentException($"({value}.Count reported an incorrect value ({value.Count})");
        }
    }
}
