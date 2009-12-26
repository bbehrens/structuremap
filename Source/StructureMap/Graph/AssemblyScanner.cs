using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using StructureMap.TypeRules;
using StructureMap.Util;

namespace StructureMap.Graph
{
    public class TypePool
    {
        private readonly Cache<Assembly, Type[]> _types = new Cache<Assembly, Type[]>();

        public TypePool(PluginGraph graph)
        {
            _types.OnMissing = assembly =>
            {
                try
                {
                    return assembly.GetExportedTypes();
                }
                catch (Exception ex)
                {
                    graph.Log.RegisterError(170, ex, assembly.FullName);
                    return new Type[0];
                }
            };
        }

        public IEnumerable<Type> For(IEnumerable<Assembly> assemblies, CompositeFilter<Type> filter)
        {
            return assemblies.SelectMany(x => _types[x].Where(filter.Matches));
        }
    }


    public class AssemblyScanner : IAssemblyScanner
    {
        private readonly List<Assembly> _assemblies = new List<Assembly>();
        private readonly CompositeFilter<Type> _filter = new CompositeFilter<Type>();

        private readonly List<IHeavyweightTypeScanner> _heavyweightScanners = new List<IHeavyweightTypeScanner>();

        private readonly List<ITypeScanner> _scanners = new List<ITypeScanner>();

        public AssemblyScanner()
        {
            With<FamilyAttributeScanner>();
            With<PluggableAttributeScanner>();
        }

        public int Count { get { return _assemblies.Count; } }


        public void Assembly(Assembly assembly)
        {
            if (!_assemblies.Contains(assembly))
            {
                _assemblies.Add(assembly);
            }
        }

        public void Assembly(string assemblyName)
        {
            Assembly(AppDomain.CurrentDomain.Load(assemblyName));
        }

        public void With(ITypeScanner scanner)
        {
            if (_scanners.Contains(scanner)) return;

            _scanners.Add(scanner);
        }

        public void With(IHeavyweightTypeScanner heavyweightScanner)
        {
            if (_heavyweightScanners.Contains(heavyweightScanner)) return;

            _heavyweightScanners.Add(heavyweightScanner);
        }

        public void WithDefaultConventions()
        {
            With<DefaultConventionScanner>();
        }

        public void With<T>() where T : ITypeScanner, new()
        {
            _scanners.RemoveAll(scanner => scanner is T);

            ITypeScanner previous = _scanners.FirstOrDefault(scanner => scanner is T);
            if (previous == null)
            {
                With(new T());
            }
        }

        public void LookForRegistries()
        {
            With<FindRegistriesScanner>();
        }

        public void TheCallingAssembly()
        {
            Assembly callingAssembly = findTheCallingAssembly();

            if (callingAssembly != null)
            {
                _assemblies.Add(callingAssembly);
            }
        }

        public void AssemblyContainingType<T>()
        {
            _assemblies.Add(typeof (T).Assembly);
        }

        public void AssemblyContainingType(Type type)
        {
            _assemblies.Add(type.Assembly);
        }

        public FindAllTypesFilter AddAllTypesOf<PLUGINTYPE>()
        {
            return AddAllTypesOf(typeof (PLUGINTYPE));
        }

        public FindAllTypesFilter AddAllTypesOf(Type pluginType)
        {
            var filter = new FindAllTypesFilter(pluginType);
            With(filter);

            return filter;
        }

        public void IgnoreStructureMapAttributes()
        {
            _scanners.RemoveAll(scanner => scanner is FamilyAttributeScanner);
            _scanners.RemoveAll(scanner => scanner is PluggableAttributeScanner);
        }


        public void Exclude(Func<Type, bool> exclude)
        {
            _filter.Excludes += exclude;
        }

        public void ExcludeNamespace(string nameSpace)
        {
            Exclude(type => type.IsInNamespace(nameSpace));
        }

        public void ExcludeNamespaceContainingType<T>()
        {
            ExcludeNamespace(typeof (T).Namespace);
        }

        public void Include(Func<Type, bool> predicate)
        {
            _filter.Includes += predicate;
        }

        public void IncludeNamespace(string nameSpace)
        {
            Include(type => type.IsInNamespace(nameSpace));
        }

        public void IncludeNamespaceContainingType<T>()
        {
            IncludeNamespace(typeof (T).Namespace);
        }

        public void ExcludeType<T>()
        {
            Exclude(type => type == typeof (T));
        }

        public void ConnectImplementationsToTypesClosing(Type openGenericType)
        {
            With(new GenericConnectionScanner(openGenericType));
        }

        public void AssembliesFromApplicationBaseDirectory()
        {
            AssembliesFromApplicationBaseDirectory(a => true);
        }

        public void AssembliesFromApplicationBaseDirectory(Predicate<Assembly> assemblyFilter)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            AssembliesFromPath(baseDirectory, assemblyFilter);
            string binPath = AppDomain.CurrentDomain.SetupInformation.PrivateBinPath;
            if (Directory.Exists(binPath))
            {
                AssembliesFromPath(binPath, assemblyFilter);
            }
        }

        public void AssembliesFromPath(string path)
        {
            AssembliesFromPath(path, a => true);
        }

        public void AssembliesFromPath(string path, Predicate<Assembly> assemblyFilter)
        {
            IEnumerable<string> assemblyPaths = Directory.GetFiles(path).Where(file =>
                                                                               Path.GetExtension(file).Equals(
                                                                                   ".exe",
                                                                                   StringComparison.OrdinalIgnoreCase)
                                                                               ||
                                                                               Path.GetExtension(file).Equals(
                                                                                   ".dll",
                                                                                   StringComparison.OrdinalIgnoreCase));

            foreach (string assemblyPath in assemblyPaths)
            {
                Assembly assembly = null;
                try
                {
                    assembly = System.Reflection.Assembly.LoadFrom(assemblyPath);
                }
                catch
                {
                }
                if (assembly != null && assemblyFilter(assembly)) Assembly(assembly);
            }
        }

        internal void ScanForAll(PluginGraph pluginGraph)
        {
            //TypeMapBuilder heavyweightScan = configureHeavyweightScan();

            pluginGraph.Types.For(_assemblies, _filter).Each(type =>
            {
                _scanners.Each(x => x.Process(type, pluginGraph));
            });

            //performHeavyweightScan(pluginGraph, heavyweightScan);
        }


        private TypeMapBuilder configureHeavyweightScan()
        {
            var typeMapBuilder = new TypeMapBuilder();
            if (_heavyweightScanners.Count > 0)
            {
                With(typeMapBuilder);
            }
            return typeMapBuilder;
        }

        [Obsolete]
        private void performHeavyweightScan(PluginGraph pluginGraph, TypeMapBuilder typeMapBuilder)
        {
            IEnumerable<TypeMap> typeMaps = typeMapBuilder.GetTypeMaps();
            _heavyweightScanners.ForEach(scanner => scanner.Process(pluginGraph, typeMaps));
            typeMapBuilder.Dispose();
        }

        public bool Contains(string assemblyName)
        {
            foreach (Assembly assembly in _assemblies)
            {
                if (assembly.GetName().Name == assemblyName)
                {
                    return true;
                }
            }

            return false;
        }

        private static Assembly findTheCallingAssembly()
        {
            var trace = new StackTrace(false);

            Assembly thisAssembly = System.Reflection.Assembly.GetExecutingAssembly();
            Assembly callingAssembly = null;
            for (int i = 0; i < trace.FrameCount; i++)
            {
                StackFrame frame = trace.GetFrame(i);
                Assembly assembly = frame.GetMethod().DeclaringType.Assembly;
                if (assembly != thisAssembly)
                {
                    callingAssembly = assembly;
                    break;
                }
            }
            return callingAssembly;
        }
    }
}