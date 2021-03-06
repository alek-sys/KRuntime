// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
#if K10
using System.Threading;
using System.Runtime.Loader;
#endif
using System.Threading.Tasks;
using Microsoft.Framework.Runtime.Common.CommandLine;

namespace klr.hosting
{
    internal static class RuntimeBootstrapper
    {
        private static readonly ConcurrentDictionary<string, object> _assemblyLoadLocks =
                new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Assembly> _assemblyCache
                = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        private static readonly char[] _libPathSeparator = new[] { ';' };

        public static int Execute(string[] args)
        {
            // If we're a console host then print exceptions to stderr
            var printExceptionsToStdError = Environment.GetEnvironmentVariable("KRE_CONSOLE_HOST") == "1";

            try
            {
                return ExecuteAsync(args).Result;
            }
            catch (Exception ex)
            {
                if (printExceptionsToStdError)
                {
                    PrintErrors(ex);
                    return 1;
                }

                throw;
            }
        }

        private static void PrintErrors(Exception ex)
        {
            var enableTrace = Environment.GetEnvironmentVariable("KRE_TRACE") == "1";

            while (ex != null)
            {
                if (ex is TargetInvocationException ||
                    ex is AggregateException)
                {
                    // Skip these exception messages as they are
                    // generic
                }
                else
                {
                    if (enableTrace)
                    {
                        Console.Error.WriteLine(ex);
                    }
                    else
                    {
                        Console.Error.WriteLine(ex.Message);
                    }
                }

                ex = ex.InnerException;
            }
        }

        public static Task<int> ExecuteAsync(string[] args)
        {
            var enableTrace = Environment.GetEnvironmentVariable("KRE_TRACE") == "1";
#if NET45
            // TODO: Make this pluggable and not limited to the console logger
            if (enableTrace)
            {
                var listener = new ConsoleTraceListener();
                Trace.Listeners.Add(listener);
                Trace.AutoFlush = true;
            }
#endif
            var app = new CommandLineApplication(throwOnUnexpectedArg: false);
            app.Name = "klr";
            var optionLib = app.Option("--lib <LIB_PATHS>", "Paths used for library look-up",
                CommandOptionType.MultipleValue);
            app.HelpOption("-?|-h|--help");
            app.VersionOption("--version", GetVersion());
            app.Execute(args);

            if (!app.IsShowingInformation && !app.RemainingArguments.Any())
            {
                app.ShowHelp();
            }

            if (app.IsShowingInformation)
            {
                return Task.FromResult(0);
            }

            // Resolve the lib paths
            string[] searchPaths = ResolveSearchPaths(optionLib.Values, app.RemainingArguments);

            Func<string, Assembly> loader = _ => null;
            Func<Stream, Assembly> loadStream = _ => null;
            Func<string, Assembly> loadFile = _ => null;

            Func<AssemblyName, Assembly> loaderCallback = assemblyName =>
            {
                string name = assemblyName.Name;

                // Skip resource assemblies
                if (name.EndsWith(".resources"))
                {
                    return null;
                }

                // If the assembly was already loaded use it
                Assembly assembly;
                if (_assemblyCache.TryGetValue(name, out assembly))
                {
                    return assembly;
                }

                var loadLock = _assemblyLoadLocks.GetOrAdd(name, new object());
                try
                {
                    // Concurrently loading the assembly might result in two distinct instances of the same assembly 
                    // being loaded. This was observed when loading via Assembly.LoadStream. Prevent this by locking on the name.
                    lock (loadLock)
                    {
                        if (_assemblyCache.TryGetValue(name, out assembly))
                        {
                            // This would succeed in case the thread was previously waiting on the lock when assembly 
                            // load was in progress
                            return assembly;
                        }

                        assembly = loader(name) ?? ResolveHostAssembly(loadFile, searchPaths, name);

                        if (assembly != null)
                        {
#if K10
                            ExtractAssemblyNeutralInterfaces(assembly, loadStream);
#endif
                            _assemblyCache[name] = assembly;
                        }
                    }
                }
                finally
                {
                    _assemblyLoadLocks.TryRemove(name, out loadLock);
                }

                return assembly;
            };
#if K10
            var loaderImpl = new DelegateAssemblyLoadContext(loaderCallback);
            loadStream = assemblyStream => loaderImpl.LoadStream(assemblyStream, pdbStream: null);
            loadFile = path => loaderImpl.LoadFile(path);

            AssemblyLoadContext.InitializeDefaultContext(loaderImpl);
            
            if (loaderImpl.EnableMultiCoreJit())
            {
                loaderImpl.StartMultiCoreJitProfile("startup.prof");
            }
#else
            var loaderImpl = new LoaderEngine();
            loadStream = assemblyStream => loaderImpl.LoadStream(assemblyStream, pdbStream: null);
            loadFile = path => loaderImpl.LoadFile(path);

            ResolveEventHandler handler = (sender, a) =>
            {
                // Special case for retargetable assemblies on desktop
                if (a.Name.EndsWith("Retargetable=Yes"))
                {
                    return Assembly.Load(a.Name);
                }

                return loaderCallback(new AssemblyName(a.Name));
            };

            AppDomain.CurrentDomain.AssemblyResolve += handler;
            AppDomain.CurrentDomain.AssemblyLoad += (object sender, AssemblyLoadEventArgs loadedArgs) =>
            {
                // Skip loading interfaces for dynamic assemblies
                if (loadedArgs.LoadedAssembly.IsDynamic)
                {
                    return;
                }

                ExtractAssemblyNeutralInterfaces(loadedArgs.LoadedAssembly, loadStream);
            };
#endif

            try
            {
                var assembly = Assembly.Load(new AssemblyName("klr.host"));

                // Loader impl
                // var loaderEngine = new DefaultLoaderEngine(loaderImpl);
                var loaderEngineType = assembly.GetType("klr.host.DefaultLoaderEngine");
                var loaderEngine = Activator.CreateInstance(loaderEngineType, loaderImpl);

                // The following code is doing:
                // var loaderContainer = new klr.host.LoaderContainer();
                // var libLoader = new klr.host.PathBasedAssemblyLoader(loaderEngine, searchPaths);
                // loaderContainer.AddLoader(libLoader);
                // var bootstrapper = new klr.host.Bootstrapper(loaderContainer, loaderEngine);
                // bootstrapper.Main(bootstrapperArgs);

                var loaderContainerType = assembly.GetType("klr.host.LoaderContainer");
                var pathBasedLoaderType = assembly.GetType("klr.host.PathBasedAssemblyLoader");

                var loaderContainer = Activator.CreateInstance(loaderContainerType);
                var libLoader = Activator.CreateInstance(pathBasedLoaderType, new object[] { loaderEngine, searchPaths });

                MethodInfo addLoaderMethodInfo = loaderContainerType.GetTypeInfo().GetDeclaredMethod("AddLoader");
                var disposable = (IDisposable)addLoaderMethodInfo.Invoke(loaderContainer, new[] { libLoader });
                var loaderContainerLoadMethodInfo = loaderContainerType.GetTypeInfo().GetDeclaredMethod("Load");

                loader = (Func<string, Assembly>)loaderContainerLoadMethodInfo.CreateDelegate(typeof(Func<string, Assembly>), loaderContainer);

                var bootstrapperType = assembly.GetType("klr.host.Bootstrapper");
                var mainMethod = bootstrapperType.GetTypeInfo().GetDeclaredMethod("Main");
                var bootstrapper = Activator.CreateInstance(bootstrapperType, loaderContainer, loaderEngine);

                try
                {
                    var bootstrapperArgs = new object[]
                    {
                        app.RemainingArguments.ToArray()
                    };

                    var task = (Task<int>)mainMethod.Invoke(bootstrapper, bootstrapperArgs);

                    return task.ContinueWith(async (t, state) =>
                    {
                        // Dispose the host
                        ((IDisposable)state).Dispose();

#if NET45
                        AppDomain.CurrentDomain.AssemblyResolve -= handler;
#endif
                        return await t;
                    },
                    disposable).Unwrap();
                }
                catch
                {
                    // If we throw synchronously then dispose then rethtrow
                    disposable.Dispose();
                    throw;
                }
            }
            catch
            {
#if NET45
                AppDomain.CurrentDomain.AssemblyResolve -= handler;
#endif
                throw;
            }
        }

        private static string[] ResolveSearchPaths(IEnumerable<string> libPaths, List<string> remainingArgs)
        {
            var searchPaths = new List<string>();

            var defaultLibPath = Environment.GetEnvironmentVariable("KRE_DEFAULT_LIB");

            if (!string.IsNullOrEmpty(defaultLibPath))
            {
                // Add the default lib folder if specified
                searchPaths.AddRange(ExpandSearchPath(defaultLibPath));
            }

            // Add the expanded search libs to the list of paths
            searchPaths.AddRange(libPaths.SelectMany(ExpandSearchPath));

            // If a .dll or .exe is specified then turn this into
            // --lib {path to dll/exe} [dll/exe name]
            if (remainingArgs.Any())
            {
                var application = remainingArgs[0];
                var extension = Path.GetExtension(application);

                if (!string.IsNullOrEmpty(extension) &&
                    extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Add the directory to the list of search paths
                    searchPaths.Add(Path.GetDirectoryName(application));

                    // Modify the argument to be the dll/exe name
                    remainingArgs[0] = Path.GetFileNameWithoutExtension(application);
                }
            }

            return searchPaths.ToArray();
        }

        private static IEnumerable<string> ExpandSearchPath(string libPath)
        {
            // Expand ; separated arguments
            return libPath.Split(_libPathSeparator, StringSplitOptions.RemoveEmptyEntries)
                          .Select(Path.GetFullPath);
        }

        private static void ExtractAssemblyNeutralInterfaces(Assembly assembly, Func<Stream, Assembly> load)
        {
            // Embedded assemblies end with .dll
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(".dll"))
                {
                    var assemblyName = Path.GetFileNameWithoutExtension(name);

                    if (_assemblyCache.ContainsKey(assemblyName))
                    {
                        continue;
                    }


                    var neutralAssemblyStream = assembly.GetManifestResourceStream(name);

                    _assemblyCache[assemblyName] = load(neutralAssemblyStream);
                }
            }
        }

        private static Assembly ResolveHostAssembly(Func<string, Assembly> loadFile, IList<string> searchPaths, string name)
        {
            foreach (var searchPath in searchPaths)
            {
                var path = Path.Combine(searchPath, name + ".dll");

                if (File.Exists(path))
                {
                    return loadFile(path);
                }
            }

            return null;
        }

        private static string GetVersion()
        {
            var assembly = typeof(RuntimeBootstrapper).GetTypeInfo().Assembly;
            var assemblyInformationalVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (assemblyInformationalVersionAttribute == null)
            {
                return assembly.GetName().Version.ToString();
            }
            return assemblyInformationalVersionAttribute.InformationalVersion;
        }
    }
}
