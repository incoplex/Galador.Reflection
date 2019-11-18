﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Collections.Concurrent;

namespace Galador.Reflection.Utils
{
    /// <summary>
    /// A utility class to list all loaded assemblies and be notified when new one are loaded.
    /// </summary>
    public static class KnownAssemblies
    {
        static KnownAssemblies()
        {
            var domain = AppDomain.CurrentDomain;

            foreach (var ass in FilterOk(domain.GetAssemblies()))
                assemblies[ass.GetName().Name] = ass;

            domain.AssemblyLoad += (o, e) =>
            {
                var loaded = new[] { e.LoadedAssembly };
                foreach (var ass in FilterOk(loaded))
                {
                    assemblies[ass.GetName().Name] = ass;
                    AssemblyLoaded?.Invoke(ass);
                }
            };

            IEnumerable<Assembly> FilterOk(IEnumerable<Assembly> source)
            {
                if (source == null)
                    yield break;
                foreach (var ass in source)
                {
                    if (ass == null)
                        continue;
                    try
                    {
                        var dt = ass.DefinedTypes;
                    }
                    catch
                    {
                        Log.Warning(typeof(KnownAssemblies).FullName, $"Couldn't get Types from {ass.GetName().Name})");
                        continue;
                    }
                    yield return ass;
                }
            }
        }

        static ConcurrentDictionary<string, Assembly> assemblies = new ConcurrentDictionary<string, Assembly>();


        /// <summary>
        /// Enumerate all the currently loaded assembly.
        /// </summary>
        public static IEnumerable<Assembly> Current => assemblies.Values;

#pragma warning disable 67
        /// <summary>
        /// Occurs when an assembly is loaded.
        /// </summary>
        public static event Action<Assembly> AssemblyLoaded;
#pragma warning restore 67

        /// <summary>
        /// Lookup a type by type name and assembly name.
        /// </summary>
        /// <param name="typeName">Name of the type.</param>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns>A type or null.</returns>
        public static Type GetType(string typeName, string assemblyName)
        {
            if (assemblyName == null)
                return Type.GetType(typeName);

            assemblies.TryGetValue(assemblyName, out var candidate);
            if (candidate != null)
                return candidate.GetType(typeName);

            return null;
        }
    }
}
