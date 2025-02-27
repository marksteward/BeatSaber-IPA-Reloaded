﻿#nullable enable
using IPA.Config;
using IPA.Loader.Features;
using IPA.Logging;
using IPA.Utilities;
using Mono.Cecil;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Version = SemVer.Version;
using SemVer;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using System.Diagnostics;
using IPA.AntiMalware;
#if NET4
using Task = System.Threading.Tasks.Task;
using TaskEx = System.Threading.Tasks.Task;
#endif
#if NET3
using Net3_Proxy;
using Path = Net3_Proxy.Path;
using File = Net3_Proxy.File;
using Directory = Net3_Proxy.Directory;
#endif

namespace IPA.Loader
{
    /// <summary>
    /// A type to manage the loading of plugins.
    /// </summary>

    internal partial class PluginLoader
    {
        internal static PluginMetadata SelfMeta = null!;

        internal static Task LoadTask() =>
            TaskEx.Run(() =>
        {
            YeetIfNeeded();

            var sw = Stopwatch.StartNew();

            LoadMetadata();

            sw.Stop();
            Logger.loader.Info($"Loading metadata took {sw.Elapsed}");
            sw.Reset();

            // old loader system
#if false
            Resolve();
            InitFeatures();
            ComputeLoadOrder();
            FilterDisabled();
            FilterWithoutFiles();

            ResolveDependencies();
#endif

            sw.Start();

            // Features contribute to load order considerations
            InitFeatures();
            DoOrderResolution();

            sw.Stop();
            Logger.loader.Info($"Calculating load order took {sw.Elapsed}");
        });

        internal static void YeetIfNeeded()
        {
            string pluginDir = UnityGame.PluginsPath;

            if (SelfConfig.YeetMods_ && UnityGame.IsGameVersionBoundary)
            {
                var oldPluginsName = Path.Combine(UnityGame.InstallPath, $"Old {UnityGame.OldVersion} Plugins");
                var newPluginsName = Path.Combine(UnityGame.InstallPath, $"Old {UnityGame.GameVersion} Plugins");

                if (Directory.Exists(oldPluginsName))
                    Directory.Delete(oldPluginsName, true);
                Directory.Move(pluginDir, oldPluginsName);
                if (Directory.Exists(newPluginsName))
                    Directory.Move(newPluginsName, pluginDir);
                else
                    _ = Directory.CreateDirectory(pluginDir);
            }
        }

        internal static List<PluginMetadata> PluginsMetadata = new();
        internal static List<PluginMetadata> DisabledPlugins = new();

        private static readonly Regex embeddedTextDescriptionPattern = new(@"#!\[(.+)\]", RegexOptions.Compiled | RegexOptions.Singleline);

        internal static void LoadMetadata()
        {
            string[] plugins = Directory.GetFiles(UnityGame.PluginsPath, "*.dll");

            try
            {
                var selfMeta = new PluginMetadata
                {
                    Assembly = Assembly.GetExecutingAssembly(),
                    File = new FileInfo(Path.Combine(UnityGame.InstallPath, "IPA.exe")),
                    PluginType = null,
                    IsSelf = true
                };

                string manifest;
                using (var manifestReader =
                    new StreamReader(
                        selfMeta.Assembly.GetManifestResourceStream(typeof(PluginLoader), "manifest.json") ??
                        throw new InvalidOperationException()))
                    manifest = manifestReader.ReadToEnd();

                selfMeta.Manifest = JsonConvert.DeserializeObject<PluginManifest>(manifest);

                PluginsMetadata.Add(selfMeta);
                SelfMeta = selfMeta;
            }
            catch (Exception e)
            {
                Logger.loader.Critical("Error loading own manifest");
                Logger.loader.Critical(e);
            }

            using var resolver = new CecilLibLoader();
            resolver.AddSearchDirectory(UnityGame.LibraryPath);
            resolver.AddSearchDirectory(UnityGame.PluginsPath);
            foreach (var plugin in plugins)
            {
                var metadata = new PluginMetadata
                {
                    File = new FileInfo(Path.Combine(UnityGame.PluginsPath, plugin)),
                    IsSelf = false
                };

                try
                {
                    var scanResult = AntiMalwareEngine.Engine.ScanFile(metadata.File);
                    if (scanResult is ScanResult.Detected)
                    {
                        Logger.loader.Warn($"Scan of {plugin} found malware; not loading");
                        continue;
                    }
                    if (!SelfConfig.AntiMalware_.RunPartialThreatCode_ && scanResult is not ScanResult.KnownSafe and not ScanResult.NotDetected)
                    {
                        Logger.loader.Warn($"Scan of {plugin} found partial threat; not loading. To load this, set AntiMalware.RunPartialThreatCode in the config.");
                        continue;
                    }

                    var pluginModule = AssemblyDefinition.ReadAssembly(metadata.File.FullName, new ReaderParameters
                    {
                        ReadingMode = ReadingMode.Immediate,
                        ReadWrite = false,
                        AssemblyResolver = resolver
                    }).MainModule;

                    string pluginNs = "";

                    PluginManifest? pluginManifest = null;
                    foreach (var resource in pluginModule.Resources)
                    {
                        const string manifestSuffix = ".manifest.json";
                        if (resource is not EmbeddedResource embedded ||
                            !embedded.Name.EndsWith(manifestSuffix, StringComparison.Ordinal)) continue;

                        pluginNs = embedded.Name.Substring(0, embedded.Name.Length - manifestSuffix.Length);

                        string manifest;
                        using (var manifestReader = new StreamReader(embedded.GetResourceStream()))
                            manifest = manifestReader.ReadToEnd();

                        pluginManifest = JsonConvert.DeserializeObject<PluginManifest?>(manifest);
                        break;
                    }

                    if (pluginManifest == null)
                    {
#if DIRE_LOADER_WARNINGS
                        Logger.loader.Error($"Could not find manifest.json for {Path.GetFileName(plugin)}");
#else
                        Logger.loader.Notice($"No manifest.json in {Path.GetFileName(plugin)}");
#endif
                        continue;
                    }

                    if (pluginManifest.Id == null)
                    {
                        Logger.loader.Warn($"Plugin '{pluginManifest.Name}' does not have a listed ID, using name");
                        pluginManifest.Id = pluginManifest.Name;
                    }

                    metadata.Manifest = pluginManifest;

                    void TryGetNamespacedPluginType(string ns, PluginMetadata meta)
                    {
                        foreach (var type in pluginModule.Types)
                        {
                            if (type.Namespace != ns) continue;

                            if (type.HasCustomAttributes)
                            {
                                var attr = type.CustomAttributes.FirstOrDefault(a => a.Constructor.DeclaringType.FullName == typeof(PluginAttribute).FullName);
                                if (attr != null)
                                {
                                    if (!attr.HasConstructorArguments)
                                    {
                                        Logger.loader.Warn($"Attribute plugin found in {type.FullName}, but attribute has no arguments");
                                        return;
                                    }

                                    var args = attr.ConstructorArguments;
                                    if (args.Count != 1)
                                    {
                                        Logger.loader.Warn($"Attribute plugin found in {type.FullName}, but attribute has unexpected number of arguments");
                                        return;
                                    }
                                    var rtOptionsArg = args[0];
                                    if (rtOptionsArg.Type.FullName != typeof(RuntimeOptions).FullName)
                                    {
                                        Logger.loader.Warn($"Attribute plugin found in {type.FullName}, but first argument is of unexpected type {rtOptionsArg.Type.FullName}");
                                        return;
                                    }

                                    var rtOptionsValInt = (int)rtOptionsArg.Value; // `int` is the underlying type of RuntimeOptions

                                    meta.RuntimeOptions = (RuntimeOptions)rtOptionsValInt;
                                    meta.PluginType = type;
                                    return;
                                }
                            }
                        }
                    }

                    var hint = metadata.Manifest.Misc?.PluginMainHint;

                    if (hint != null)
                    {
                        var type = pluginModule.GetType(hint);
                        if (type != null)
                            TryGetNamespacedPluginType(hint, metadata);
                    }

                    if (metadata.PluginType == null)
                        TryGetNamespacedPluginType(pluginNs, metadata);

                    if (metadata.PluginType == null)
                    {
                        Logger.loader.Error($"No plugin found in the manifest {(hint != null ? $"hint path ({hint}) or " : "")}namespace ({pluginNs}) in {Path.GetFileName(plugin)}");
                        continue;
                    }

                    Logger.loader.Debug($"Adding info for {Path.GetFileName(plugin)}");
                    PluginsMetadata.Add(metadata);
                }
                catch (Exception e)
                {
                    Logger.loader.Error($"Could not load data for plugin {Path.GetFileName(plugin)}");
                    Logger.loader.Error(e);
                    ignoredPlugins.Add(metadata, new IgnoreReason(Reason.Error)
                    {
                        ReasonText = "An error ocurred loading the data",
                        Error = e
                    });
                }
            }

            IEnumerable<string> bareManifests = Directory.GetFiles(UnityGame.PluginsPath, "*.json");
            bareManifests = bareManifests.Concat(Directory.GetFiles(UnityGame.PluginsPath, "*.manifest"));
            foreach (var manifest in bareManifests)
            {
                try
                {
                    var metadata = new PluginMetadata
                    {
                        File = new FileInfo(Path.Combine(UnityGame.PluginsPath, manifest)),
                        IsSelf = false,
                        IsBare = true,
                    };

                    metadata.Manifest = JsonConvert.DeserializeObject<PluginManifest>(File.ReadAllText(manifest));

                    if (metadata.Manifest.Files.Length < 1)
                        Logger.loader.Warn($"Bare manifest {Path.GetFileName(manifest)} does not declare any files. " +
                            $"Dependency resolution and verification cannot be completed.");

                    Logger.loader.Debug($"Adding info for bare manifest {Path.GetFileName(manifest)}");
                    PluginsMetadata.Add(metadata);
                }
                catch (Exception e)
                {
                    Logger.loader.Error($"Could not load data for bare manifest {Path.GetFileName(manifest)}");
                    Logger.loader.Error(e);
                }
            }

            foreach (var meta in PluginsMetadata)
            { // process description include
                var lines = meta.Manifest.Description.Split('\n');
                var m = embeddedTextDescriptionPattern.Match(lines[0]);
                if (m.Success)
                {
                    if (meta.IsBare)
                    {
                        Logger.loader.Warn($"Bare manifest cannot specify description file");
                        meta.Manifest.Description = string.Join("\n", lines.Skip(1).StrJP()); // ignore first line
                        continue;
                    }

                    var name = m.Groups[1].Value;
                    string description;
                    if (!meta.IsSelf)
                    {
                        // plugin type must be non-null for non-self plugins
                        var resc = meta.PluginType!.Module.Resources.Select(r => r as EmbeddedResource)
                                                                   .NonNull()
                                                                   .FirstOrDefault(r => r.Name == name);
                        if (resc == null)
                        {
                            Logger.loader.Warn($"Could not find description file for plugin {meta.Name} ({name}); ignoring include");
                            meta.Manifest.Description = string.Join("\n", lines.Skip(1).StrJP()); // ignore first line
                            continue;
                        }

                        using var reader = new StreamReader(resc.GetResourceStream());
                        description = reader.ReadToEnd();
                    }
                    else
                    {
                        using var descriptionReader = new StreamReader(meta.Assembly.GetManifestResourceStream(name));
                        description = descriptionReader.ReadToEnd();
                    }

                    meta.Manifest.Description = description;
                }
            }
        }
    }

    #region Ignore stuff
    /// <summary>
    /// An enum that represents several categories of ignore reasons that the loader may encounter.
    /// </summary>
    /// <seealso cref="IgnoreReason"/>
    public enum Reason
    {
        /// <summary>
        /// An error was thrown either loading plugin information fomr disk, or when initializing the plugin.
        /// </summary>
        /// <remarks>
        /// When this is the set <see cref="Reason"/> in an <see cref="IgnoreReason"/> structure, the member
        /// <see cref="IgnoreReason.Error"/> will contain the thrown exception.
        /// </remarks>
        Error,
        /// <summary>
        /// The plugin this reason is associated with has the same ID as another plugin whose information was
        /// already loaded.
        /// </summary>
        /// <remarks>
        /// When this is the set <see cref="Reason"/> in an <see cref="IgnoreReason"/> structure, the member
        /// <see cref="IgnoreReason.RelatedTo"/> will contain the metadata of the already loaded plugin.
        /// </remarks>
        Duplicate,
        /// <summary>
        /// The plugin this reason is associated with conflicts with another already loaded plugin.
        /// </summary>
        /// <remarks>
        /// When this is the set <see cref="Reason"/> in an <see cref="IgnoreReason"/> structure, the member
        /// <see cref="IgnoreReason.RelatedTo"/> will contain the metadata of the plugin it conflicts with.
        /// </remarks>
        Conflict,
        /// <summary>
        /// The plugin this reason is assiciated with is missing a dependency.
        /// </summary>
        /// <remarks>
        /// Since this is only given when a dependency is missing, <see cref="IgnoreReason.RelatedTo"/> will
        /// not be set.
        /// </remarks>
        Dependency,
        /// <summary>
        /// The plugin this reason is associated with was released for a game update, but is still considered
        /// present for the purposes of updating.
        /// </summary>
        Released,
        /// <summary>
        /// The plugin this reason is associated with was denied from loading by a <see cref="Features.Feature"/>
        /// that it marks.
        /// </summary>
        Feature,
        /// <summary>
        /// The plugin this reason is assoicated with is unsupported.
        /// </summary>
        /// <remarks>
        /// Currently, there is no path in the loader that emits this <see cref="Reason"/>, however there may
        /// be in the future.
        /// </remarks>
        Unsupported,
        /// <summary>
        /// One of the files that a plugin declared in its manifest is missing.
        /// </summary>
        MissingFiles
    }
    /// <summary>
    /// A structure describing the reason that a plugin was ignored.
    /// </summary>
    public struct IgnoreReason : IEquatable<IgnoreReason>
    {
        /// <summary>
        /// Gets the ignore reason, as represented by the <see cref="Loader.Reason"/> enum.
        /// </summary>
        public Reason Reason { get; }
        /// <summary>
        /// Gets the textual description of the particular ignore reason. This will typically
        /// include details about why the plugin was ignored, if it is present.
        /// </summary>
        public string? ReasonText { get; internal set; }
        /// <summary>
        /// Gets the <see cref="Exception"/> that caused this plugin to be ignored, if any.
        /// </summary>
        public Exception? Error { get; internal set; }
        /// <summary>
        /// Gets the metadata of the plugin that this ignore was related to, if any.
        /// </summary>
        public PluginMetadata? RelatedTo { get; internal set; }
        /// <summary>
        /// Initializes an <see cref="IgnoreReason"/> with the provided data.
        /// </summary>
        /// <param name="reason">the <see cref="Loader.Reason"/> enum value that describes this reason</param>
        /// <param name="reasonText">the textual description of this ignore reason, if any</param>
        /// <param name="error">the <see cref="Exception"/> that caused this <see cref="IgnoreReason"/>, if any</param>
        /// <param name="relatedTo">the <see cref="PluginMetadata"/> this reason is related to, if any</param>
        public IgnoreReason(Reason reason, string? reasonText = null, Exception? error = null, PluginMetadata? relatedTo = null)
        {
            Reason = reason;
            ReasonText = reasonText;
            Error = error;
            RelatedTo = relatedTo;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
            => obj is IgnoreReason ir && Equals(ir);
        /// <summary>
        /// Compares this <see cref="IgnoreReason"/> with <paramref name="other"/> for equality.
        /// </summary>
        /// <param name="other">the reason to compare to</param>
        /// <returns><see langword="true"/> if the two reasons compare equal, <see langword="false"/> otherwise</returns>
        public bool Equals(IgnoreReason other)
            => Reason == other.Reason && ReasonText == other.ReasonText
            && Error == other.Error && RelatedTo == other.RelatedTo;

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            int hashCode = 778404373;
            hashCode = (hashCode * -1521134295) + Reason.GetHashCode();
            hashCode = (hashCode * -1521134295) + ReasonText?.GetHashCode() ?? 0;
            hashCode = (hashCode * -1521134295) + Error?.GetHashCode() ?? 0;
            hashCode = (hashCode * -1521134295) + RelatedTo?.GetHashCode() ?? 0;
            return hashCode;
        }

        /// <summary>
        /// Checks if two <see cref="IgnoreReason"/>s are equal.
        /// </summary>
        /// <param name="left">the first <see cref="IgnoreReason"/> to compare</param>
        /// <param name="right">the second <see cref="IgnoreReason"/> to compare</param>
        /// <returns><see langword="true"/> if the two reasons compare equal, <see langword="false"/> otherwise</returns>
        public static bool operator ==(IgnoreReason left, IgnoreReason right)
            => left.Equals(right);

        /// <summary>
        /// Checks if two <see cref="IgnoreReason"/>s are not equal.
        /// </summary>
        /// <param name="left">the first <see cref="IgnoreReason"/> to compare</param>
        /// <param name="right">the second <see cref="IgnoreReason"/> to compare</param>
        /// <returns><see langword="true"/> if the two reasons are not equal, <see langword="false"/> otherwise</returns>
        public static bool operator !=(IgnoreReason left, IgnoreReason right)
            => !(left == right);
    }
    #endregion

    internal partial class PluginLoader
    {
        // keep track of these for the updater; it should still be able to update mods not loaded
        // the thing -> the reason
        internal static Dictionary<PluginMetadata, IgnoreReason> ignoredPlugins = new();

#if false
        internal static void Resolve()
        { // resolves duplicates and conflicts, etc
            PluginsMetadata.Sort((a, b) => b.Version.CompareTo(a.Version));

            var ids = new HashSet<string>();
            var ignore = new Dictionary<PluginMetadata, IgnoreReason>();
            var resolved = new List<PluginMetadata>(PluginsMetadata.Count);
            foreach (var meta in PluginsMetadata)
            {
                if (meta.Id != null)
                {
                    if (ids.Contains(meta.Id))
                    {
                        Logger.loader.Warn($"Found duplicates of {meta.Id}, using newest");
                        var ireason = new IgnoreReason(Reason.Duplicate)
                        {
                            ReasonText = $"Duplicate entry of same ID ({meta.Id})",
                            RelatedTo = resolved.First(p => p.Id == meta.Id)
                        };
                        ignore.Add(meta, ireason);
                        ignoredPlugins.Add(meta, ireason);
                        continue; // because of sorted order, hightest order will always be the first one
                    }

                    bool processedLater = false;
                    foreach (var meta2 in PluginsMetadata)
                    {
                        if (ignore.ContainsKey(meta2)) continue;
                        if (meta == meta2)
                        {
                            processedLater = true;
                            continue;
                        }

                        if (!meta2.Manifest.Conflicts.ContainsKey(meta.Id)) continue;

                        var range = meta2.Manifest.Conflicts[meta.Id];
                        if (!range.IsSatisfied(meta.Version)) continue;

                        Logger.loader.Warn($"{meta.Id}@{meta.Version} conflicts with {meta2.Id}");

                        if (processedLater)
                        {
                            Logger.loader.Warn($"Ignoring {meta2.Name}");
                            ignore.Add(meta2, new IgnoreReason(Reason.Conflict)
                            {
                                ReasonText = $"{meta.Id}@{meta.Version} conflicts with {meta2.Id}",
                                RelatedTo = meta
                            });
                        }
                        else
                        {
                            Logger.loader.Warn($"Ignoring {meta.Name}");
                            ignore.Add(meta, new IgnoreReason(Reason.Conflict)
                            {
                                ReasonText = $"{meta2.Id}@{meta2.Version} conflicts with {meta.Id}",
                                RelatedTo = meta2
                            });
                            break;
                        }
                    }
                }

                if (ignore.TryGetValue(meta, out var reason))
                {
                    ignoredPlugins.Add(meta, reason);
                    continue;
                }
                if (meta.Id != null)
                    ids.Add(meta.Id);

                resolved.Add(meta);
            }

            PluginsMetadata = resolved;
        }

        private static void FilterDisabled()
        {
            var enabled = new List<PluginMetadata>(PluginsMetadata.Count);

            var disabled = DisabledConfig.Instance.DisabledModIds;
            foreach (var meta in PluginsMetadata)
            {
                if (disabled.Contains(meta.Id ?? meta.Name))
                    DisabledPlugins.Add(meta);
                else
                    enabled.Add(meta);
            }

            PluginsMetadata = enabled;
        }

        private static void FilterWithoutFiles()
        {
            var enabled = new List<PluginMetadata>(PluginsMetadata.Count);

            foreach (var meta in PluginsMetadata)
            {
                var passed = true;
                foreach (var file in meta.AssociatedFiles)
                {
                    if (!file.Exists)
                    {
                        passed = false;
                        ignoredPlugins.Add(meta, new IgnoreReason(Reason.MissingFiles)
                        {
                            ReasonText = $"File {Utils.GetRelativePath(file.FullName, UnityGame.InstallPath)} (declared by {meta.Name}) does not exist"
                        });
                        Logger.loader.Warn($"File {Utils.GetRelativePath(file.FullName, UnityGame.InstallPath)}" +
                            $" (declared by {meta.Name}) does not exist! Mod installation is incomplete, not loading it.");
                        break;
                    }
                }

                if (passed)
                    enabled.Add(meta);
            }

            PluginsMetadata = enabled;
        }

        internal static void ComputeLoadOrder()
        {
#if DEBUG
            Logger.loader.Debug(string.Join(", ", PluginsMetadata.Select(p => p.ToString()).StrJP()));
#endif

            static bool InsertInto(HashSet<PluginMetadata> root, PluginMetadata meta, bool isRoot = false)
            { // this is slow, and hella recursive
                bool inserted = false;
                foreach (var sr in root)
                {
                    inserted = inserted || InsertInto(sr.Dependencies, meta);

                    if (meta.Id != null)
                    {
                        if (sr.Manifest.Dependencies.ContainsKey(meta.Id))
                            inserted = inserted || sr.Dependencies.Add(meta);
                        else if (sr.Manifest.LoadAfter.Contains(meta.Id))
                            inserted = inserted || sr.LoadsAfter.Add(meta);
                    }
                    if (sr.Id != null)
                        if (meta.Manifest.LoadBefore.Contains(sr.Id))
                            inserted = inserted || sr.LoadsAfter.Add(meta);
                }

                if (isRoot)
                {
                    foreach (var sr in root)
                    {
                        InsertInto(meta.Dependencies, sr);

                        if (sr.Id != null)
                        {
                            if (meta.Manifest.Dependencies.ContainsKey(sr.Id))
                                meta.Dependencies.Add(sr);
                            else if (meta.Manifest.LoadAfter.Contains(sr.Id))
                                meta.LoadsAfter.Add(sr);
                        }
                        if (meta.Id != null)
                            if (sr.Manifest.LoadBefore.Contains(meta.Id))
                                meta.LoadsAfter.Add(sr);
                    }

                    root.Add(meta);
                }

                return inserted;
            }

            var pluginTree = new HashSet<PluginMetadata>();
            foreach (var meta in PluginsMetadata)
                InsertInto(pluginTree, meta, true);

            static void DeTree(List<PluginMetadata> into, HashSet<PluginMetadata> tree)
            {
                foreach (var st in tree)
                    if (!into.Contains(st))
                    {
                        DeTree(into, st.Dependencies);
                        DeTree(into, st.LoadsAfter);
                        into.Add(st);
                    }
            }

            PluginsMetadata = new List<PluginMetadata>();
            DeTree(PluginsMetadata, pluginTree);

#if DEBUG
            Logger.loader.Debug(string.Join(", ", PluginsMetadata.Select(p => p.ToString()).StrJP()));
#endif
        }

        internal static void ResolveDependencies()
        {
            var metadata = new List<PluginMetadata>();
            var pluginsToLoad = new Dictionary<string, Version>();
            var disabledLookup = DisabledPlugins.NonNull(m => m.Id).ToDictionary(m => m.Id, m => m.Version);
            foreach (var meta in PluginsMetadata)
            {
                bool ignoreBcNoLoader = true;
                var missingDeps = new List<(string id, Range version, bool disabled)>();
                foreach (var dep in meta.Manifest.Dependencies)
                {
#if DEBUG
                    Logger.loader.Debug($"Looking for dependency {dep.Key} with version range {dep.Value.Intersect(new SemVer.Range("*.*.*"))}");
#endif
                    if (dep.Key == SelfMeta.Id)
                        ignoreBcNoLoader = false;

                    if (pluginsToLoad.ContainsKey(dep.Key) && dep.Value.IsSatisfied(pluginsToLoad[dep.Key]))
                        continue;

                    if (disabledLookup.ContainsKey(dep.Key) && dep.Value.IsSatisfied(disabledLookup[dep.Key]))
                    {
                        Logger.loader.Warn($"Dependency {dep.Key} was found, but disabled. Disabling {meta.Name} too.");
                        missingDeps.Add((dep.Key, dep.Value, true));
                    }
                    else
                    {
                        Logger.loader.Warn($"{meta.Name} is missing dependency {dep.Key}@{dep.Value}");
                        missingDeps.Add((dep.Key, dep.Value, false));
                    }
                }

                if (meta.PluginType != null && !meta.IsSelf && !meta.IsBare && ignoreBcNoLoader)
                {
                    ignoredPlugins.Add(meta, new IgnoreReason(Reason.Dependency)
                    {
                        ReasonText = "BSIPA Plugin does not reference BSIPA!"
                    });
                    for (int i = 0; i < 20; i++)
                    {
                        Logger.loader.Warn($"HEY {meta.Id} YOU DEPEND ON BSIPA SO DEPEND ON BSIPA");
                    }
                    continue;
                }

                if (missingDeps.Count == 0)
                {
                    metadata.Add(meta);
                    if (meta.Id != null)
                        pluginsToLoad.Add(meta.Id, meta.Version);
                }
                else if (missingDeps.Any(t => !t.disabled))
                { // missing deps
                    ignoredPlugins.Add(meta, new IgnoreReason(Reason.Dependency)
                    {
                        ReasonText = $"Missing dependencies {string.Join(", ", missingDeps.Where(t => !t.disabled).Select(t => $"{t.id}@{t.version}").StrJP())}"
                    });
                }
                else
                {
                    DisabledPlugins.Add(meta);
                    DisabledConfig.Instance.DisabledModIds.Add(meta.Id ?? meta.Name);
                }
            }

            DisabledConfig.Instance.Changed();
            PluginsMetadata = metadata;
        }
#endif

        internal static void DoOrderResolution()
        {
            PluginsMetadata.Sort((a, b) => a.Version.CompareTo(b.Version));

            var metadataCache = new Dictionary<string, (PluginMetadata Meta, bool Enabled)>(PluginsMetadata.Count);
            var pluginsToProcess = new List<PluginMetadata>(PluginsMetadata.Count);

            var disabledIds = DisabledConfig.Instance.DisabledModIds;
            var disabledPlugins = new List<PluginMetadata>();

            // build metadata cache
            foreach (var meta in PluginsMetadata)
            {
                if (!metadataCache.TryGetValue(meta.Id, out var existing))
                {
                    if (disabledIds.Contains(meta.Id))
                    {
                        metadataCache.Add(meta.Id, (meta, false));
                        disabledPlugins.Add(meta);
                    }
                    else
                    {
                        metadataCache.Add(meta.Id, (meta, true));
                        pluginsToProcess.Add(meta);
                    }
                }
                else
                {
                    Logger.loader.Warn($"Found duplicates of {meta.Id}, using newest");
                    ignoredPlugins.Add(meta, new(Reason.Duplicate)
                    {
                        ReasonText = $"Duplicate entry of same ID ({meta.Id})",
                        RelatedTo = existing.Meta
                    });
                }
            }

            // preprocess LoadBefore into LoadAfter
            foreach (var kvp in metadataCache)
            { // we iterate the metadata cache because it contains both disabled and enabled plugins
                var loadBefore = kvp.Value.Meta.Manifest.LoadBefore;
                foreach (var id in loadBefore)
                {
                    if (metadataCache.TryGetValue(id, out var plugin))
                    {
                        // if the id exists in our metadata cache, make sure it knows to load after the plugin in kvp
                        _ = plugin.Meta.LoadsAfter.Add(kvp.Value.Meta);
                    }
                }
            }

            var loadedPlugins = new Dictionary<string, (PluginMetadata Meta, bool Disabled, bool Ignored)>();
            var outputOrder = new List<PluginMetadata>(PluginsMetadata.Count);

            {
                bool TryResolveId(string id, [MaybeNullWhen(false)] out PluginMetadata meta, out bool disabled, out bool ignored)
                {
                    meta = null;
                    disabled = false;
                    ignored = true;
                    if (loadedPlugins.TryGetValue(id, out var foundMeta))
                    {
                        meta = foundMeta.Meta;
                        disabled = foundMeta.Disabled;
                        ignored = foundMeta.Ignored;
                        return true;
                    }
                    if (metadataCache!.TryGetValue(id, out var plugin))
                    {
                        disabled = !plugin.Enabled;
                        meta = plugin.Meta;
                        if (!disabled)
                        {
                            Resolve(plugin.Meta, ref disabled, out ignored);
                        }
                        loadedPlugins.Add(id, (plugin.Meta, disabled, ignored));
                        return true;
                    }
                    return false;
                }

                void Resolve(PluginMetadata plugin, ref bool disabled, out bool ignored)
                {
                    // if this method is being called, this is the first and only time that it has been called for this plugin.

                    ignored = false;

                    // perform file existence check before attempting to load dependencies
                    foreach (var file in plugin.AssociatedFiles)
                    {
                        if (!file.Exists)
                        {
                            ignoredPlugins.Add(plugin, new IgnoreReason(Reason.MissingFiles)
                            {
                                ReasonText = $"File {Utils.GetRelativePath(file.FullName, UnityGame.InstallPath)} does not exist"
                            });
                            Logger.loader.Warn($"File {Utils.GetRelativePath(file.FullName, UnityGame.InstallPath)}" +
                                $" (declared by {plugin.Name}) does not exist! Mod installation is incomplete, not loading it.");
                            ignored = true;
                            return;
                        }
                    }

                    // first load dependencies
                    var dependsOnSelf = false;
                    foreach (var dep in plugin.Manifest.Dependencies)
                    {
                        if (dep.Key == SelfMeta.Id)
                            dependsOnSelf = true;
                        if (!TryResolveId(dep.Key, out var depMeta, out var depDisabled, out var depIgnored))
                        {
                            Logger.loader.Warn($"Dependency '{dep.Key}@{dep.Value}' for '{plugin.Id}' does not exist; ignoring '{plugin.Id}'");
                            ignoredPlugins.Add(plugin, new(Reason.Dependency)
                            {
                                ReasonText = $"Dependency '{dep.Key}@{dep.Value}' not found",
                            });
                            ignored = true;
                            return;
                        }
                        // make a point to propagate ignored
                        if (depIgnored)
                        {
                            Logger.loader.Warn($"Dependency '{dep.Key}' for '{plugin.Id}' previously ignored; ignoring '{plugin.Id}'");
                            ignoredPlugins.Add(plugin, new(Reason.Dependency)
                            {
                                ReasonText = $"Dependency '{dep.Key}' ignored",
                                RelatedTo = depMeta
                            });
                            ignored = true;
                            return;
                        }
                        // make a point to propagate disabled
                        if (depDisabled)
                        {
                            Logger.loader.Warn($"Dependency '{dep.Key}' for '{plugin.Id}' disabled; disabling");
                            disabledPlugins!.Add(plugin);
                            _ = disabledIds!.Add(plugin.Id);
                            disabled = true;
                        }

                        // we found our dep, lets save the metadata and keep going
                        _ = plugin.Dependencies.Add(depMeta);
                    }

                    // make sure the plugin depends on the loader (assuming it actually needs to)
                    if (!dependsOnSelf && !plugin.IsSelf && !plugin.IsBare)
                    {
                        Logger.loader.Warn($"Plugin '{plugin.Id}' does not depend on any particular loader version; assuming its incompatible");
                        ignoredPlugins.Add(plugin, new(Reason.Dependency)
                        {
                            ReasonText = "Does not depend on any loader version, so it is assumed to be incompatible",
                            RelatedTo = SelfMeta
                        });
                        ignored = true;
                        return;
                    }

                    // exit early if we've decided we need to be disabled
                    if (disabled)
                        return;

                    // handle LoadsAfter populated by Features processing
                    foreach (var loadAfter in plugin.LoadsAfter)
                    {
                        if (TryResolveId(loadAfter.Id, out _, out _, out _))
                        {
                            // do nothing, because the plugin is already in the LoadsAfter set
                        }
                    }

                    // then handle loadafters
                    foreach (var id in plugin.Manifest.LoadAfter)
                    {
                        if (TryResolveId(id, out var meta, out var depDisabled, out var depIgnored) && !depIgnored)
                        {
                            // we only want to make sure to loadafter if its not ignored
                            // if its disabled, we still wanna track it where possible
                            _ = plugin.LoadsAfter.Add(meta);
                        }
                    }

                    // after we handle dependencies and loadafters, then check conflicts
                    foreach (var conflict in plugin.Manifest.Conflicts)
                    {
                        if (TryResolveId(conflict.Key, out var meta, out var conflDisabled, out var conflIgnored) && !conflIgnored && !conflDisabled)
                        {
                            // the conflict is only *actually* a problem if it is both not ignored and not disabled
                            Logger.loader.Warn($"Plugin '{plugin.Id}' conflicts with {meta.Id}@{meta.Version}; ignoring '{plugin.Id}'");
                            ignoredPlugins.Add(plugin, new(Reason.Conflict)
                            {
                                ReasonText = $"Conflicts with {meta.Id}@{meta.Version}",
                                RelatedTo = meta
                            });
                            ignored = true;
                            return;
                        }
                    }

                    // we can now load the current plugin
                    outputOrder!.Add(plugin);

                    // loadbefores have already been preprocessed into loadafters
                }

                // run TryResolveId over every plugin, which recursively calculates load order
                foreach (var plugin in pluginsToProcess)
                {
                    _ = TryResolveId(plugin.Id, out _, out _, out _);
                }
                // by this point, outputOrder contains the full load order
            }

            DisabledConfig.Instance.Changed();
            DisabledPlugins = disabledPlugins;
            PluginsMetadata = outputOrder;
        }

        internal static void InitFeatures()
        {
            foreach (var meta in PluginsMetadata)
            {
                foreach (var feature in meta.Manifest.Features.Select(f => new Feature.Instance(meta, f.Key, f.Value)))
                {
                    if (feature.TryGetDefiningPlugin(out var plugin) && plugin == null)
                    { // this is a DefineFeature, so we want to initialize it early
                        if (!feature.TryCreate(out var inst))
                        {
                            Logger.features.Error($"Error evaluating {feature.Name}: {inst.InvalidMessage}");
                        }
                        else
                        {
                            meta.InternalFeatures.Add(inst);
                        }
                    }
                    else
                    { // this is literally any other feature, so we want to delay its initialization
                        _ = meta.UnloadedFeatures.Add(feature);
                    }
                }
            }

            // at this point we have pre-initialized all features, so we can go ahead and use them to add stuff to the dep resolver
            foreach (var meta in PluginsMetadata)
            {
                foreach (var feature in meta.UnloadedFeatures)
                {
                    if (feature.TryGetDefiningPlugin(out var plugin))
                    {
                        if (plugin != meta && plugin != null)
                        { // if the feature is not applied to the defining feature
                            _ = meta.LoadsAfter.Add(plugin);
                        }

                        if (plugin != null)
                        {
                            plugin.CreateFeaturesWhenLoaded.Add(feature);
                        }
                    }
                    else
                    {
                        Logger.features.Warn($"No such feature {feature.Name}");
                    }
                }
            }
        }

        internal static void ReleaseAll(bool full = false)
        {
            if (full)
            {
                ignoredPlugins = new();
            }
            else
            {
                foreach (var m in PluginsMetadata)
                    ignoredPlugins.Add(m, new IgnoreReason(Reason.Released));
                foreach (var m in ignoredPlugins.Keys)
                { // clean them up so we can still use the metadata for updates
                    m.InternalFeatures.Clear();
                    m.PluginType = null;
                    m.Assembly = null!;
                }
            }
            PluginsMetadata = new List<PluginMetadata>();
            DisabledPlugins = new List<PluginMetadata>();
            Feature.Reset();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        internal static void Load(PluginMetadata meta)
        {
            if (meta is { Assembly: null, PluginType: not null })
                meta.Assembly = Assembly.LoadFrom(meta.File.FullName);
        }

        internal static PluginExecutor? InitPlugin(PluginMetadata meta, IEnumerable<PluginMetadata> alreadyLoaded)
        {
            if (meta.Manifest.GameVersion != UnityGame.GameVersion)
                Logger.loader.Warn($"Mod {meta.Name} developed for game version {meta.Manifest.GameVersion}, so it may not work properly.");

            if (meta.IsSelf)
                return new PluginExecutor(meta, PluginExecutor.Special.Self);

            foreach (var dep in meta.Dependencies)
            {
                if (alreadyLoaded.Contains(dep)) continue;

                // otherwise...

                if (ignoredPlugins.TryGetValue(dep, out var reason))
                { // was added to the ignore list
                    ignoredPlugins.Add(meta, new IgnoreReason(Reason.Dependency)
                    {
                        ReasonText = $"Dependency was ignored at load time: {reason.ReasonText}",
                        RelatedTo = dep
                    });
                }
                else
                { // was not added to ignore list
                    ignoredPlugins.Add(meta, new IgnoreReason(Reason.Dependency)
                    {
                        ReasonText = $"Dependency was not already loaded at load time, but was also not ignored",
                        RelatedTo = dep
                    });
                }

                return null;
            }

            if (meta.IsBare)
                return new PluginExecutor(meta, PluginExecutor.Special.Bare);

            Load(meta);

            PluginExecutor exec;
            try
            {
                exec = new PluginExecutor(meta);
            }
            catch (Exception e)
            {
                Logger.loader.Error($"Error creating executor for {meta.Name}");
                Logger.loader.Error(e);
                return null;
            }

            foreach (var feature in meta.Features)
            {
                try
                {
                    feature.BeforeInit(meta);
                }
                catch (Exception e)
                {
                    Logger.loader.Critical($"Feature errored in {nameof(Feature.BeforeInit)}:");
                    Logger.loader.Critical(e);
                }
            }

            try
            {
                exec.Create();
            }
            catch (Exception e)
            {
                Logger.loader.Error($"Could not init plugin {meta.Name}");
                Logger.loader.Error(e);
                ignoredPlugins.Add(meta, new IgnoreReason(Reason.Error)
                {
                    ReasonText = "Error ocurred while initializing",
                    Error = e
                });
                return null;
            }

            // TODO: make this new features system behave better wrt DynamicInit plugins
            foreach (var feature in meta.CreateFeaturesWhenLoaded)
            {
                if (!feature.TryCreate(out var inst))
                {
                    Logger.features.Warn($"Could not create instance of feature {feature.Name}: {inst.InvalidMessage}");
                }
                else
                {
                    feature.AppliedTo.InternalFeatures.Add(inst);
                    _ = feature.AppliedTo.UnloadedFeatures.Remove(feature);
                }
            }
            meta.CreateFeaturesWhenLoaded.Clear(); // if a plugin is loaded twice, for the moment, we don't want to create the feature twice

            foreach (var feature in meta.Features)
                try
                {
                    feature.AfterInit(meta, exec.Instance);
                }
                catch (Exception e)
                {
                    Logger.loader.Critical($"Feature errored in {nameof(Feature.AfterInit)}:");
                    Logger.loader.Critical(e);
                }

            return exec;
        }

        internal static bool IsFirstLoadComplete { get; private set; }

        internal static List<PluginExecutor> LoadPlugins()
        {
            DisabledPlugins.ForEach(Load); // make sure they get loaded into memory so their metadata and stuff can be read more easily

            var list = new List<PluginExecutor>();
            var loaded = new HashSet<PluginMetadata>();
            foreach (var meta in PluginsMetadata)
            {
                try
                {
                    var exec = InitPlugin(meta, loaded);
                    if (exec != null)
                    {
                        list.Add(exec);
                        _ = loaded.Add(meta);
                    }
                }
                catch (Exception e)
                {
                    Logger.log.Critical($"Uncaught exception while loading pluign {meta.Name}:");
                    Logger.log.Critical(e);
                }
            }

            // TODO: should this be somewhere else?
            IsFirstLoadComplete = true;

            return list;
        }
    }
}