﻿using Galador.Reflection.Serialization.IO;
using Galador.Reflection.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Galador.Reflection.Serialization
{
    public sealed class TypeData
    {
        internal TypeData() { }

        #region Initialize(RuntimeType)

        internal void Initialize(RuntimeType type)
        {
            target = type;

            IsSupported = type.IsSupported;
            if (!IsSupported)
                return;

            Kind = type.Kind;
            switch (type.Kind)
            {
                case PrimitiveType.None:
                case PrimitiveType.Object:
                    break;
                case PrimitiveType.Type:
                case PrimitiveType.String:
                case PrimitiveType.Bytes:
                    IsReference = true;
                    IsSealed = true;
                    return;
                default:
                    return;
            }

            BaseType = type.BaseType?.TypeData();
            Element = type.Element?.TypeData();
            Surrogate = type.Surrogate?.SurrogateType.TypeData();
            FullName = type.FullName;
            Assembly = type.Assembly;

            IsArray = type.IsArray;
            IsSealed = type.IsSealed;
            IsReference = type.IsReference;
            IsEnum = type.IsEnum;
            IsInterface = type.IsInterface;
            HasConverter = type.Converter != null;
            IsISerializable = type.IsISerializable;
            IsGenericParameter = type.IsGenericParameter;
            IsGeneric = type.IsGeneric;
            IsGenericTypeDefinition = type.IsGenericTypeDefinition;
            IsNullable = type.IsNullable;
            ArrayRank = type.ArrayRank;

            GenericParameterIndex = type.GenericParameterIndex;
            if (type.GenericParameters != null)
                GenericParameters = type.GenericParameters.Select(x => x.TypeData()).ToList().AsReadOnly();

            foreach (var m in type.Members)
            {
                Members.Add(new Member(this)
                {
                    Name = m.Name,
                    Type = m.Type.TypeData(),
                });
            }
            CollectionType = type.CollectionType;
            Collection1 = type.Collection1?.TypeData();
            Collection2 = type.Collection2?.TypeData();
        }

        #endregion

        #region MakeGenericTypeData()

        TypeData MakeGenericTypeData(IReadOnlyList<TypeData> parameters)
        {
            if (!IsSupported)
                return this;

            if (IsGenericParameter)
                return parameters[this.GenericParameterIndex];

            if (!IsGeneric)
                return this;

            var result = new TypeData()
            {
                IsSupported = true,
                Kind = Kind,
                IsSealed = IsSealed,
                IsReference = IsReference,
                IsEnum = IsEnum,
                IsInterface = IsInterface,
                HasConverter = HasConverter,
                IsISerializable = IsISerializable,
                IsGeneric = true,
                IsGenericTypeDefinition = false,
                IsNullable = IsNullable,
                Element = this,
                GenericParameters = GenericParameters.Select(x => x.MakeGenericTypeData(parameters)).ToList().AsReadOnly(),
            };
            result.BaseType = BaseType?.MakeGenericTypeData(parameters);
            result.Surrogate = Surrogate?.MakeGenericTypeData(parameters);

            if (Surrogate == null
                && !IsInterface
                && !IsArray && !IsEnum
                && !IsGenericParameter)
            {
                foreach (var m in Members)
                {
                    var rm = new Member(result);
                    rm.Name = m.Name;
                    rm.Type = m.Type.MakeGenericTypeData(parameters);
                    result.Members.Add(rm);
                }
                result.CollectionType = CollectionType;
                result.Collection1 = Collection1?.MakeGenericTypeData(parameters);
                result.Collection2 = Collection2?.MakeGenericTypeData(parameters);
            }
            return result;
        }

        #endregion

        #region Read() Write()

        internal void Read(Reader reader, IPrimitiveReader input)
        {
            var flags = input.ReadVInt();
            if (flags == 0)
            {
                IsSupported = false;
                return;
            }
            IsSupported = true;
            IsInterface = (flags & (1 << 1)) != 0;
            IsISerializable = (flags & (1 << 2)) != 0;
            IsReference = (flags & (1 << 3)) != 0;
            IsSealed = (flags & (1 << 4)) != 0;
            IsArray = (flags & (1 << 5)) != 0;
            IsNullable = (flags & (1 << 6)) != 0;
            IsEnum = (flags & (1 << 7)) != 0;
            IsGeneric = (flags & (1 << 8)) != 0;
            IsGenericParameter = (flags & (1 << 9)) != 0;
            IsGenericTypeDefinition = (flags & (1 << 10)) != 0;
            HasConverter = (flags & (1 << 11)) != 0;
            Kind = (PrimitiveType)((flags >> 12) & 0b11111);
            CollectionType = (RuntimeCollectionType)((flags >> 17) & 0b111);
            switch (Kind)
            {
                case PrimitiveType.None:
                case PrimitiveType.Object:
                    break;
                default:
                    return;
            }

            Element = (TypeData)reader.ReadImpl(Reader.AType);
            Surrogate = (TypeData)reader.ReadImpl(Reader.AType);
            int genCount = (int)input.ReadVInt();
            if (genCount > 0)
            {
                var glp = new List<TypeData>();
                for (int i = 0; i < genCount; i++)
                {
                    var data = (TypeData)reader.ReadImpl(Reader.AType);
                    glp.Add(data);
                }
                GenericParameters = glp.AsReadOnly();
            }

            if (!IsGeneric || IsGenericTypeDefinition)
            {
                FullName = (string)reader.ReadImpl(Reader.AString);
                Assembly = (string)reader.ReadImpl(Reader.AString);
                GenericParameterIndex = (int)input.ReadVInt();
                BaseType = (TypeData)reader.ReadImpl(Reader.AType);
                ArrayRank = (int)input.ReadVInt();

                if (Surrogate == null
                   && !IsInterface
                   && !IsArray && !IsEnum
                   && !IsGenericParameter)
                {
                    int mc = (int)input.ReadVInt();
                    for (int i = 0; i < mc; i++)
                    {
                        var m = new Member(this);
                        m.Name = (string)reader.ReadImpl(Reader.AString);
                        m.Type = (TypeData)reader.ReadImpl(Reader.AType);
                        Members.Add(m);
                    }
                    Collection1 = (TypeData)reader.ReadImpl(Reader.AType);
                    Collection2 = (TypeData)reader.ReadImpl(Reader.AType);
                }
            }
            else
            {
                if (Surrogate == null && Element.Surrogate != null)
                    Surrogate = Element.Surrogate.MakeGenericTypeData(GenericParameters);

                BaseType = Element?.BaseType?.MakeGenericTypeData(GenericParameters);

                if (Surrogate == null
                    && !IsInterface
                    && !IsArray && !IsEnum
                    && !IsGenericParameter)
                {
                    for (int i = 0; i < Element.Members.Count; i++)
                    {
                        var em = Element.Members[i];
                        var m = new Member(this);
                        m.Name = em.Name;
                        m.Type = em.Type.MakeGenericTypeData(GenericParameters);
                        Members.Add(m);
                    }
                    Collection1 = Element.Collection1?.MakeGenericTypeData(GenericParameters);
                    Collection2 = Element.Collection2?.MakeGenericTypeData(GenericParameters);
                }
            }
        }

        internal void Write(Writer writer, IPrimitiveWriter output)
        {
            if (!IsSupported)
            {
                output.WriteVInt(0);
                return;
            }
            var flags = 1;
            if (IsInterface) flags |= 1 << 1;
            if (IsISerializable) flags |= 1 << 2;
            if (IsReference) flags |= 1 << 3;
            if (IsSealed) flags |= 1 << 4;
            if (IsArray) flags |= 1 << 5;
            if (IsNullable) flags |= 1 << 6;
            if (IsEnum) flags |= 1 << 7;
            if (IsGeneric) flags |= 1 << 8;
            if (IsGenericParameter) flags |= 1 << 9;
            if (IsGenericTypeDefinition) flags |= 1 << 10;
            if (HasConverter) flags |= 1 << 11;
            flags |= (int)Kind << 12;
            flags |= (int)CollectionType << 17;
            switch (Kind)
            {
                case PrimitiveType.None:
                case PrimitiveType.Object:
                    break;
                default:
                    return;
            }
            output.WriteVInt(flags);

            writer.Write(Context.RType, Element);
            writer.Write(Context.RType, Surrogate);
            output.WriteVInt(GenericParameters?.Count ?? 0);
            if (GenericParameters != null)
                for (int i = 0; i < GenericParameters.Count; i++)
                    writer.Write(Context.RType, GenericParameters[i]);

            if (!IsGeneric || IsGenericTypeDefinition)
            {
                writer.Write(Context.RString, FullName);
                writer.Write(Context.RString, Assembly);
                output.WriteVInt(GenericParameterIndex);
                writer.Write(Context.RType, BaseType);
                output.WriteVInt(ArrayRank);

                if (Surrogate == null 
                    && !IsInterface
                    && !IsArray && !IsEnum
                    && !IsGenericParameter)
                {
                    output.WriteVInt(Members.Count);
                    for (int i = 0; i < Members.Count; i++)
                    {
                        var m = Members[i];
                        writer.Write(Context.RString, m.Name);
                        writer.Write(Context.RType, m.Type);
                    }
                    writer.Write(Context.RType, Collection1);
                    writer.Write(Context.RType, Collection2);
                }
            }
        }

        #endregion

        #region public: RuntimeType()

        public RuntimeType RuntimeType()
        {
            if (target == null && !resolved)
            {
                resolved = true;
                try
                {
                    switch (Kind)
                    {
                        case PrimitiveType.None:
                            break;
                        case PrimitiveType.Object:
                            if (IsArray)
                            {
                                var type = Element.RuntimeType()?.Type;
                                if (ArrayRank == 1)
                                    type = type?.MakeArrayType();
                                else if (ArrayRank > 1)
                                    type = type?.MakeArrayType(ArrayRank);
                                target = Serialization.RuntimeType.GetType(type);
                            }
                            else if (IsGenericParameter)
                            {
                                // nothing
                            }
                            else if (IsGeneric && !IsGenericTypeDefinition)
                            {
                                var type = Element.RuntimeType()?.Type;
                                var parameters = GenericParameters.Select(x => x.RuntimeType()?.Type).ToArray();
                                if (type != null && parameters.All(x => x != null))
                                    target = Serialization.RuntimeType.GetType(type.MakeGenericType(parameters));
                            }
                            else
                            {
                                target = Serialization.RuntimeType.GetType(FullName, Assembly);
                            }
                            break;
                        default:
                            target = Serialization.RuntimeType.GetType(PrimitiveConverter.GetType(Kind));
                            break;
                    }
                }
                catch (SystemException se) { Log.Error(se); }
            }
            return target;
        }
        RuntimeType target;
        bool resolved;

        #endregion

        #region RuntimeMembers

        public IEnumerable<Member> RuntimeMembers
        {
            get
            {
                if (BaseType != null)
                    foreach (var m in BaseType.RuntimeMembers)
                        yield return m;
                foreach (var m in Members)
                    yield return m;
            }
        }

        #endregion

        public bool IsSupported { get; private set; }
        public PrimitiveType Kind { get; private set; }
        public bool IsInterface { get; private set; }
        public bool IsISerializable { get; private set; }
        public bool IsReference { get; private set; }
        public bool IsSealed { get; private set; }
        public bool IsArray { get; private set; }
        public bool IsNullable { get; private set; }
        public bool IsEnum { get; private set; }
        public bool IsGeneric { get; private set; }
        public bool IsGenericParameter { get; private set; }
        public bool IsGenericTypeDefinition { get; private set; }
        public bool HasConverter { get; set; }

        public string FullName { get; private set; }
        public string Assembly { get; private set; }
        public IReadOnlyList<TypeData> GenericParameters { get; private set; }

        public TypeData BaseType { get; private set; }
        public TypeData Element { get; private set; }
        public TypeData Surrogate { get; set; } // not strictly for deserialization, but needed for C# Code generation, and should be present in stream
        public int ArrayRank { get; private set; }
        public int GenericParameterIndex { get; private set; }

        public RuntimeCollectionType CollectionType { get; private set; }
        public TypeData Collection1 { get; private set; }
        public TypeData Collection2 { get; private set; }

        public MemberList<Member> Members { get; } = new MemberList<Member>();
        public class Member : IMember
        {
            internal Member(TypeData owner) { DeclaringType = owner; }
            public TypeData DeclaringType { get; }
            public string Name { get; internal set; }
            public TypeData Type { get; internal set; }
        }
    }
}
