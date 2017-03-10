﻿using Galador.Reflection.IO;
using Galador.Reflection.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Galador.Reflection.Utils
{
    /// <summary>
    /// Each <see cref="FastType"/> instance is associate with a particular .NET <see cref="System.Type"/>.
    /// It provides access to optimized members and constructor method, using System.Emit whenever possible for top performance.
    /// </summary>
    public sealed class FastType
    {
        FastType() { }

        #region GetType()

        /// <summary>
        /// Gets the <see cref="FastType"/> associated with <typeparamref name="T"/> type.
        /// </summary>
        public static FastType GetType<T>() { return GetType(typeof(T)); }

        /// <summary>
        /// Gets the <see cref="FastType"/> associated with <paramref name="type"/> type.
        /// </summary>
        public static FastType GetType(Type type)
        {
            if (type == null)
                return null;
            lock (sReflectCache)
            {
                FastType result;
                if (!sReflectCache.TryGetValue(type, out result))
                {
                    result = new FastType();
                    sReflectCache[type] = result;
                    result.Initialize(type);
                }
                return result;
            }
        }
        static Dictionary<Type, FastType> sReflectCache = new Dictionary<System.Type, FastType>();

        /// <summary>
        /// Get the <see cref="FastType"/> associated with each primitive type. Except for object, where it returns null.
        /// </summary>
        public static FastType GetType(PrimitiveType kind)
        {
            var type = PrimitiveConverter.GetType(kind);
            return GetType(type);
        }

        #endregion

        #region TryConstruct() SetConstructor()

        /// <summary>
        /// Will create and return a new instance of <see cref="Type"/> associated to this <see cref="FastType"/>
        /// By using either the default constructor (i.e. constructor with no parameter or where all parameters have 
        /// a default value) or creating a so called "uninitialized object". It might return null if it fails.
        /// </summary>
        public object TryConstruct()
        {
            if (IsGenericMeta || IsAbstract || IsIgnored)
                return null;

#if __NET__ || __NETCORE__
            if (fastCtor != null)
                return fastCtor();
#endif
            if (emtpy_constructor != null)
                return emtpy_constructor.Invoke(empty_params);

            if (!IsReference)
                return Activator.CreateInstance(Type);

#if __PCL__
            throw new PlatformNotSupportedException("PCL"); 
#elif __NETCORE__
            return null;
#else
            return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(Type);
#endif
        }

        FastMethod emtpy_constructor;
        object[] empty_params;
#if __NET__ || __NETCORE__
        Func<object> fastCtor;
#endif
        void SetConstructor()
        {
            var ctor = Type.TryGetConstructors().OrderBy(x => x.GetParameters().Length).FirstOrDefault();
            if (ctor == null)
            {
#if __NET__ || __NETCORE__
                if (Type.GetTypeInfo().IsValueType)
                    fastCtor = EmitHelper.CreateParameterlessConstructorHandler(Type);
#endif
                return;
            }

            var ps = ctor.GetParameters();
            var cargs = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                if (!p.HasDefaultValue)
                    return;
                cargs[i] = p.DefaultValue;
            }
            emtpy_constructor = new FastMethod(ctor);
            empty_params = cargs;
        }

        #endregion

        /// <summary>
        /// Whether or not this is associated with a <see cref="Type"/> which is a pointer, delegate or IntPtr.
        /// </summary>
        internal bool IsIgnored { get; private set; }

        /// <summary>
        /// Whether this is a real class (<c>false</c>), or a generic one missing arguments (<c>true</c>).
        /// </summary>
        public bool IsGenericMeta { get; private set; }

        /// <summary>
        /// Whether this is an abstract class or not.
        /// </summary>
        public bool IsAbstract { get; set; }

        /// <summary>
        /// Whether or not this is a type passed by reference.
        /// </summary>
        public bool IsReference { get; private set; }

        /// <summary>
        /// The possible well know type, as an enum.
        /// </summary>
        public PrimitiveType Kind { get; private set; }

        /// <summary>
        /// The type associated with this instance.
        /// </summary>
        public Type Type { get; private set; }

        /// <summary>
        /// Gets the <see cref="FastType"/> associated with the BaseType.
        /// </summary>
        public FastType BaseType { get; private set; }

        /// <summary>
        /// Whether or not this type is defined in mscorlib. Assembly name can be omitted for such type when serializing.
        /// Also they don't need be generated when creating serialized type code.
        /// </summary>
        public bool IsMscorlib { get; private set; }

        /// <summary>
        /// Check whether a type is from mscorlib base library, or not. Impacting the information needed for serialization.
        /// </summary>
        public static bool IsFromMscorlib(Type type) { return type.GetTypeInfo().Assembly == MSCORLIB; }
        internal static readonly Assembly MSCORLIB = typeof(object).GetTypeInfo().Assembly;

        #region IsUndefined()

        /// <summary>
        /// Whether <paramref name="type"/> is a generic type with generic argument, such as <c>List&lt;&gt;</c>.
        /// </summary>
        public static bool IsUndefined(Type type)
        {
            if (type.IsGenericParameter)
                return true;
#if __PCL__
                throw new PlatformNotSupportedException("PCL");
#else
            var ti = type.GetTypeInfo();
            if (!ti.IsGenericType)
                return false;
            return ti.GetGenericArguments().Any(x => x.GetTypeInfo().IsGenericParameter);
#endif
        }

        #endregion

        #region Initialize()

        void Initialize(Type type)
        {
            Type = type;
            var ti = type.GetTypeInfo();

            Kind = PrimitiveConverter.GetPrimitiveType(type);
            IsReference = !ti.IsValueType;
            BaseType = GetType(Type.GetTypeInfo().BaseType);
            IsMscorlib = IsFromMscorlib(type);
            IsAbstract = type.GetTypeInfo().IsAbstract;
            IsGenericMeta = IsUndefined(type);

            if (type.IsPointer)
                IsIgnored = true;
            else if (typeof(Delegate).IsBaseClass(type) || type == typeof(IntPtr) || type == typeof(Enum))
                IsIgnored = true;
            if (IsIgnored)
                return;

            if (!type.IsArray && !ti.IsEnum)
                SetConstructor();
        }

        #endregion

        /// <summary>
        /// Enumerate all <see cref="FastMember"/> of this class and all of its base classes.
        /// </summary>
        public IEnumerable<FastMember> GetRuntimeMembers()
        {
            var p = this;
            while (p != null)
            {
                foreach (var m in p.DeclaredMembers)
                    yield return m;
                p = p.BaseType;
            }
        }

        #region DeclaredMembers

        /// <summary>
        /// Members list for this <see cref="Type"/> as <see cref="FastMember"/>.
        /// </summary>
        public MemberList<FastMember> DeclaredMembers
        {
            get
            {
                if (members == null)
                    lock (this)
                        if (members == null)
                        {
                            var result = new MemberList<FastMember>();
                            var ti = Type.GetTypeInfo();
                            foreach (var pi in ti.DeclaredFields)
                            {
                                var mt = FastType.GetType(pi.FieldType);
                                if (mt.IsIgnored)
                                    continue;
                                var m = new FastMember(pi);
                                result.Add(m);
                            }
                            foreach (var pi in ti.DeclaredProperties)
                            {
                                if (pi.GetMethod == null || pi.GetMethod.GetParameters().Length != 0)
                                    continue;
                                var mt = FastType.GetType(pi.PropertyType);
                                if (mt.IsIgnored)
                                    continue;
                                var m = new FastMember(pi);
                                result.Add(m);
                            }
                            members = result;
                        }
                return members;
            }
        }
        MemberList<FastMember> members;

        #endregion

        #region DeclaredMethods

        /// <summary>
        /// Gets all the declared methods of <see cref="Type"/> as <see cref="FastMethod"/>.
        /// </summary>
        public IReadOnlyList<FastMethod> DeclaredMethods
        {
            get
            {
                if (methods == null)
                    lock (this)
                        if (methods == null)
                            methods = Type.GetTypeInfo().DeclaredMethods.Select(x => new FastMethod(x)).ToArray();
                return methods;
            }
        }
        FastMethod[] methods;

        #endregion

        #region DeclaredConstructors

        /// <summary>
        /// Gets all the declared constructors of <see cref="Type"/> as <see cref="FastMethod"/>.
        /// </summary>
        public IReadOnlyList<FastMethod> DeclaredConstructors
        {
            get
            {
                if (ctors == null)
                    lock (this)
                        if (ctors == null)
                            ctors = Type.GetTypeInfo().DeclaredConstructors.Select(x => new FastMethod(x)).ToArray();
                return ctors;
            }
        }
        FastMethod[] ctors;

        #endregion
    }

    #region class FastMember

    /// <summary>
    /// Represent a member of this type, i.e. a property or field that will be serialized.
    /// Also this will use fast member accessor generated with Emit on platform supporting it.
    /// </summary>
    public sealed class FastMember : IMember
    {
        internal FastMember(MemberInfo member)
        {
            Name = member.Name;
            Member = member;
            if (member is FieldInfo)
            {
                var pi = (FieldInfo)member;
                IsField = true;
                Type = FastType.GetType(pi.FieldType);
                IsPublic = pi.IsPublic;
                CanSet = !pi.IsLiteral;
                IsStatic = pi.IsStatic;
            }
            else
            {
                var pi = (PropertyInfo)member;
                Type = FastType.GetType(pi.PropertyType);
                IsPublic = pi.GetMethod.IsPublic;
                IsField = false;
                CanSet = pi.SetMethod != null;
                IsStatic = pi.GetMethod.IsStatic;
            }
            InitializeAccessor();
            InitializeStructAccessor();
        }

        /// <summary>
        /// This is the member name for the member, i.e. <see cref="MemberInfo.Name"/>.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Whether or not this describe a static member.
        /// </summary>
        public bool IsStatic { get; private set; }

        /// <summary>
        /// This is the info for the declared type of this member, i.e. either of
        /// <see cref="PropertyInfo.PropertyType"/> or <see cref="FieldInfo.FieldType"/>.
        /// </summary>
        public FastType Type { get; private set; }

        /// <summary>
        /// Whether this is a public member or not
        /// </summary>
        public bool IsPublic { get; private set; }

        /// <summary>
        /// Whether this is a field or a property
        /// </summary>
        public bool IsField { get; private set; }

        /// <summary>
        /// Whether this member can be set. Will be <c>false</c> if the member is a property without setter, 
        /// or if the field is a literal (set at compile time), <c>true</c> otherwise.
        /// </summary>
        public bool CanSet { get; private set; }

        /// <summary>
        /// Return the reflection member associated with this instance, be it a <see cref="FieldInfo"/> or <see cref="PropertyInfo"/>.
        /// </summary>
        public MemberInfo Member { get; private set; }

        // performance fields, depends on platform
#if __NET__ || __NETCORE__
        Action<object, object> setter;
        Func<object, object> getter;
        Action<object, Guid> setterGuid;
        Action<object, bool> setterBool;
        Action<object, char> setterChar;
        Action<object, byte> setterByte;
        Action<object, sbyte> setterSByte;
        Action<object, short> setterInt16;
        Action<object, ushort> setterUInt16;
        Action<object, int> setterInt32;
        Action<object, uint> setterUInt32;
        Action<object, long> setterInt64;
        Action<object, ulong> setterUInt64;
        Action<object, float> setterSingle;
        Action<object, double> setterDouble;
        Action<object, decimal> setterDecimal;
        Func<object, Guid> getterGuid;
        Func<object, bool> getterBool;
        Func<object, char> getterChar;
        Func<object, byte> getterByte;
        Func<object, sbyte> getterSByte;
        Func<object, short> getterInt16;
        Func<object, ushort> getterUInt16;
        Func<object, int> getterInt32;
        Func<object, uint> getterUInt32;
        Func<object, long> getterInt64;
        Func<object, ulong> getterUInt64;
        Func<object, float> getterSingle;
        Func<object, double> getterDouble;
        Func<object, decimal> getterDecimal;
#else
        PropertyInfo pInfo;
        FieldInfo fInfo;
#endif

        #region InitializeStructAccessor() InitializeAccessor()

#if __NET__ || __NETCORE__
        void InitializeStructAccessor()
        {
            switch (Type.Kind)
            {
                default:
                case PrimitiveType.None:
                case PrimitiveType.Object:
                case PrimitiveType.String:
                case PrimitiveType.Bytes:
                    break;
                case PrimitiveType.Guid:
                    InitFastGetter<Guid>(Member, ref getterGuid, ref setterGuid);
                    break;
                case PrimitiveType.Bool:
                    InitFastGetter<bool>(Member, ref getterBool, ref setterBool);
                    break;
                case PrimitiveType.Char:
                    InitFastGetter<char>(Member, ref getterChar, ref setterChar);
                    break;
                case PrimitiveType.Byte:
                    InitFastGetter<byte>(Member, ref getterByte, ref setterByte);
                    break;
                case PrimitiveType.SByte:
                    InitFastGetter<sbyte>(Member, ref getterSByte, ref setterSByte);
                    break;
                case PrimitiveType.Int16:
                    InitFastGetter<short>(Member, ref getterInt16, ref setterInt16);
                    break;
                case PrimitiveType.UInt16:
                    InitFastGetter<ushort>(Member, ref getterUInt16, ref setterUInt16);
                    break;
                case PrimitiveType.Int32:
                    InitFastGetter<int>(Member, ref getterInt32, ref setterInt32);
                    break;
                case PrimitiveType.UInt32:
                    InitFastGetter<uint>(Member, ref getterUInt32, ref setterUInt32);
                    break;
                case PrimitiveType.Int64:
                    InitFastGetter<long>(Member, ref getterInt64, ref setterInt64);
                    break;
                case PrimitiveType.UInt64:
                    InitFastGetter<ulong>(Member, ref getterUInt64, ref setterUInt64);
                    break;
                case PrimitiveType.Single:
                    InitFastGetter<float>(Member, ref getterSingle, ref setterSingle);
                    break;
                case PrimitiveType.Double:
                    InitFastGetter<double>(Member, ref getterDouble, ref setterDouble);
                    break;
                case PrimitiveType.Decimal:
                    InitFastGetter<decimal>(Member, ref getterDecimal, ref setterDecimal);
                    break;
            }
        }
        static void InitFastGetter<T>(MemberInfo mi, ref Func<object, T> getter, ref Action<object, T> setter)
        {
            if (mi is PropertyInfo)
            {
                InitFastGetter<T>((PropertyInfo)mi, ref getter, ref setter);
            }
            else if (mi is FieldInfo)
            {
                InitFastGetter<T>((FieldInfo)mi, ref getter, ref setter);
            }
        }
        static void InitFastGetter<T>(PropertyInfo pi, ref Func<object, T> getter, ref Action<object, T> setter)
        {
            getter = EmitHelper.CreatePropertyGetter<T>(pi);
            if (pi.SetMethod != null)
                setter = EmitHelper.CreatePropertySetter<T>(pi);
        }
        static void InitFastGetter<T>(FieldInfo pi, ref Func<object, T> getter, ref Action<object, T> setter)
        {
            if (pi.IsLiteral)
            {
                var value = (T)pi.GetValue(null);
                getter = (x) => value;
            }
            else
            {
                getter = EmitHelper.CreateFieldGetter<T>(pi);
                setter = EmitHelper.CreateFieldSetter<T>(pi);
            }
        }
#else
        void InitializeStructAccessor()
        {
        }
#endif

        void InitializeAccessor()
        {
            if (Member is PropertyInfo)
            {
                var pi = (PropertyInfo)Member;
#if __NET__ || __NETCORE__
                getter = EmitHelper.CreatePropertyGetterHandler(pi);
                if (pi.SetMethod != null)
                {
                    setter = EmitHelper.CreatePropertySetterHandler(pi);
                }
#else
                pInfo = pi;
#endif
            }
            else
            {
                var fi = (FieldInfo)Member;
                if (fi.IsLiteral)
                {
#if __NET__ || __NETCORE__
                    var value = fi.GetValue(null);
                    getter = (x) => value;
#else
                    fInfo = fi;
#endif
                }
                else
                {
#if __NET__ || __NETCORE__
                    getter = EmitHelper.CreateFieldGetterHandler(fi);
                    setter = EmitHelper.CreateFieldSetterHandler(fi);
#else
                    fInfo = fi;
#endif
                }
            }
        }

        #endregion

        #region public: GetValue() SetValue()

        /// <summary>
        /// Gets the value of this member for the given instance.
        /// </summary>
        /// <param name="instance">The instance from which to take the value.</param>
        /// <returns>The value of the member.</returns>
        public object GetValue(object instance)
        {
            if (IsStatic)
            {
                instance = null;
            }
            else
            {
                if (instance == null)
                    return null;
            }
#if __NET__ || __NETCORE__
            if (getter != null)
                return getter(instance);
#else
            if (pInfo != null && pInfo.GetMethod != null)
                return pInfo.GetValue(instance);
            if (fInfo != null)
                return fInfo.GetValue(instance);
#endif
            return null;
        }

        /// <summary>
        /// Sets the value of this member (if possible) for the given instance.
        /// </summary>
        /// <param name="instance">The instance on which the member value will be set.</param>
        /// <param name="value">The value that must be set.</param>
        /// <returns>Whether the value has been set, or not.</returns>
        public bool SetValue(object instance, object value)
        {
            if (!CanSet)
                return false;
            if (IsStatic)
            {
                instance = null;
            }
            else
            {
                if (instance == null || !Type.Type.IsInstanceOf(value))
                    return false;
            }

#if __NET__ || __NETCORE__
            if (setter != null)
            {
                setter(instance, value);
                return true;
            }
#else
            if (pInfo != null && pInfo.SetMethod != null)
            {
                pInfo.SetValue(instance, value);
                return true;
            }
            else if (fInfo != null && !fInfo.IsLiteral)
            {
                fInfo.SetValue(instance, value);
                return true;
            }
#endif
            return false;
        }

        #endregion

        #region typed known structs: Get/Set Guid/Bool/Char/...()

#if __NET__ || __NETCORE__
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool FastSet<T>(object instance, T value, Action<object, T> setter)
        {
            if (setter != null)
            {
                setter(instance, value);
                return true;
            }
            else { return false; }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static T FastGet<T>(object instance, Func<object, T> getter)
        {
            if (getter != null) { return getter(instance); }
            else { return default(T); }
        }
#else
        static T As<T>(object value) { return value is T ? (T)value : default(T); }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetGuid(object instance, Guid value)
        {
#if __NET__ || __NETCORE__
            return FastSet<Guid>(instance, value, setterGuid);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Guid GetGuid(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<Guid>(instance, getterGuid);
#else
            return As<Guid>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetBool(object instance, bool value)
        {
#if __NET__ || __NETCORE__
            return FastSet<bool>(instance, value, setterBool);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetBool(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<bool>(instance, getterBool);
#else
            return As<bool>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetChar(object instance, char value)
        {
#if __NET__ || __NETCORE__
            return FastSet<char>(instance, value, setterChar);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public char GetChar(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<char>(instance, getterChar);
#else
            return As<char>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetInt8(object instance, byte value)
        {
#if __NET__ || __NETCORE__
            return FastSet<byte>(instance, value, setterByte);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetInt8(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<byte>(instance, getterByte);
#else
            return As<byte>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetUInt8(object instance, sbyte value)
        {
#if __NET__ || __NETCORE__
            return FastSet<sbyte>(instance, value, setterSByte);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public sbyte GetUInt8(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<sbyte>(instance, getterSByte);
#else
            return As<sbyte>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetInt16(object instance, short value)
        {
#if __NET__ || __NETCORE__
            return FastSet<short>(instance, value, setterInt16);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public short GetInt16(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<short>(instance, getterInt16);
#else
            return As<short>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetUInt16(object instance, ushort value)
        {
#if __NET__ || __NETCORE__
            return FastSet<ushort>(instance, value, setterUInt16);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetUInt16(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<ushort>(instance, getterUInt16);
#else
            return As<ushort>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetInt32(object instance, int value)
        {
#if __NET__ || __NETCORE__
            return FastSet<int>(instance, value, setterInt32);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetInt32(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<int>(instance, getterInt32);
#else
            return As<int>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetUInt32(object instance, uint value)
        {
#if __NET__ || __NETCORE__
            return FastSet<uint>(instance, value, setterUInt32);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetUInt32(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<uint>(instance, getterUInt32);
#else
            return As<uint>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetInt64(object instance, long value)
        {
#if __NET__ || __NETCORE__
            return FastSet<long>(instance, value, setterInt64);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetInt64(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<long>(instance, getterInt64);
#else
            return As<long>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetUInt64(object instance, ulong value)
        {
#if __NET__ || __NETCORE__
            return FastSet<ulong>(instance, value, setterUInt64);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong GetUInt64(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<ulong>(instance, getterUInt64);
#else
            return As<ulong>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetSingle(object instance, float value)
        {
#if __NET__ || __NETCORE__
            return FastSet<float>(instance, value, setterSingle);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetSingle(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<float>(instance, getterSingle);
#else
            return As<float>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetDouble(object instance, double value)
        {
#if __NET__ || __NETCORE__
            return FastSet<double>(instance, value, setterDouble);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double GetDouble(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<double>(instance, getterDouble);
#else
            return As<double>(GetValue(instance));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetDecimal(object instance, decimal value)
        {
#if __NET__ || __NETCORE__
            return FastSet<decimal>(instance, value, setterDecimal);
#else
            return SetValue(instance, value);
#endif
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public decimal GetDecimal(object instance)
        {
#if __NET__ || __NETCORE__
            return FastGet<decimal>(instance, getterDecimal);
#else
            return As<decimal>(GetValue(instance));
#endif
        }

        #endregion
    }

    #endregion
}