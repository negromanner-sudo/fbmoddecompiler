using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using Frosty.Core;
using Frosty.Core.Mod;
using Frosty.Core.IO;
using Frosty.Hash;

namespace FbmodDecompiler
{
    class Program
    {
        private const ulong FBPROJECT_MAGIC = 0x00005954534F5246; 
        private const uint FBPROJECT_VERSION = 14; 

        private static List<EbxResourceData> ebxResources = new List<EbxResourceData>();
        private static List<ResResourceData> resResources = new List<ResResourceData>();
        private static List<ChunkResourceData> chunkResources = new List<ChunkResourceData>();
        private static List<BundleResourceData> bundleResources = new List<BundleResourceData>();
        
        private static byte[] projectIcon = null;
        private static List<byte[]> projectScreenshots = new List<byte[]>();

        private static Dictionary<int, string> bundleHashToName = new Dictionary<int, string>();
        private static Dictionary<int, string> superBundleIdToName = new Dictionary<int, string>();
        private static string defaultSuperBundleName = null;  // First valid superbundle from game

        class EbxResourceData {
            public string Name; public byte[] Data; public Guid Guid; public bool IsAdded;
            public bool HasCustomHandler; public int HandlerHash; public List<int> AddedBundles = new List<int>();
        }
        class ResResourceData {
            public string Name; public byte[] Data; public ulong ResRid; public uint ResType;
            public byte[] ResMeta; public bool IsAdded; public List<int> AddedBundles = new List<int>();
        }
        class ChunkResourceData {
            public Guid Id; public byte[] Data; public int H32; public uint RangeStart; public uint RangeEnd;
            public uint LogicalOffset; public uint LogicalSize; public int FirstMip; public bool IsAdded;
            public List<int> AddedBundles = new List<int>();
        }
        class BundleResourceData {
            public string Name; public string SuperBundleName; public int Type; public bool IsAdded;
        }

        static int Main(string[] args)
        {
            Console.WriteLine("=== Frosty FBMOD Decompiler v3.9 ===");
            Console.WriteLine("Restored Stable Baseline");
            Console.WriteLine();

            string inputPath = null, outputPath = null, gamePath = null;

            if (args.Length >= 1 && args[0] == "compile")
            {
                if (args.Length < 3) { Console.WriteLine("Usage: compile <project> <output>"); return 1; }
                CompileProject(args[1], args[2]);
                return 0;
            }

            if (args.Length >= 2) { inputPath = args[0]; outputPath = args[1]; }
            if (args.Length >= 3) { gamePath = args[2]; }

            if (string.IsNullOrEmpty(gamePath)) {
                string pathFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "path.txt");
                if (File.Exists(pathFile)) gamePath = File.ReadAllText(pathFile).Trim();
            }

            if (string.IsNullOrEmpty(inputPath) || string.IsNullOrEmpty(outputPath) || string.IsNullOrEmpty(gamePath)) {
                return 1;
            }

            try {
                Console.WriteLine($"Using game path from path.txt: {gamePath}");
                Console.WriteLine("Initializing Frosty SDK...");
                string gameExe = FindGameExecutable(gamePath);
                string profileKey = Path.GetFileNameWithoutExtension(gameExe);
                ProfilesLibrary.Initialize(new List<Profile>());
                ProfilesLibrary.Initialize(profileKey);
                TypeLibrary.Initialize();

                App.FileSystem = new FileSystem(gamePath);
                App.ResourceManager = new ResourceManager(App.FileSystem);
                App.ResourceManager.Initialize();
                App.AssetManager = new AssetManager(App.FileSystem, App.ResourceManager);
                App.AssetManager.Initialize();

                // Add to cache helper
                Action<string> AddToCache = (name) => {
                    if (string.IsNullOrEmpty(name)) return;
                    string nameNoWin32 = name.StartsWith("win32/", StringComparison.OrdinalIgnoreCase) ? name.Substring(6) : name;
                    
                    string[] variations = {
                        name,
                        name.ToLower(),
                        name.ToUpper(),
                        name.Replace("\\", "/"),
                        name.Replace("\\", "/").ToLower(),
                        nameNoWin32,
                        nameNoWin32.ToLower(),
                        Path.GetFileName(name),
                        Path.GetFileName(name).ToLower()
                    };
                    foreach (var v in variations) {
                        int h1 = Fnv1.HashString(v);
                        if (!bundleHashToName.ContainsKey(h1)) bundleHashToName[h1] = name;
                        
                        // Fnv1a variant
                        uint h1a = 0x811c9dc5;
                        foreach (char c in v) h1a = (h1a ^ (byte)c) * 0x01000193;
                        int h1ai = (int)h1a;
                        if (!bundleHashToName.ContainsKey(h1ai)) bundleHashToName[h1ai] = name;
                    }
                };

                // Build Hash Cache
                Console.WriteLine("Building exhaustive bundle hash cache...");
                foreach (var bundle in App.AssetManager.EnumerateBundles())
                {
                    AddToCache(bundle.Name);
                }

                // Build SuperBundle Cache
                Console.WriteLine("Building super bundle cache...");
                int sbIndex = 0;
                foreach (var sb in App.AssetManager.EnumerateSuperBundles())
                {
                    Console.WriteLine($"  SB[{sbIndex}]: '{sb.Name}'");
                    if (!superBundleIdToName.ContainsKey(sbIndex))
                        superBundleIdToName.Add(sbIndex, sb.Name);
                    
                    // Store first superbundle as default (guaranteed to be valid)
                    if (defaultSuperBundleName == null && !string.IsNullOrEmpty(sb.Name))
                        defaultSuperBundleName = sb.Name;

                    AddToCache(sb.Name);
                    sbIndex++;
                }
                Console.WriteLine($"Found {sbIndex} superbundles. Default: '{defaultSuperBundleName}'");

                // DISABLE AssetManager to allow EbxReader to work in "Offline Mode" 
                Console.WriteLine("Detaching asset manager for clean processing...");
                App.AssetManager = null;
                
                Console.WriteLine();

                Console.WriteLine("Loading mod file...");
                string modTitle = "Unknown", modAuthor = "Unknown", modCategory = "", modVersion = "1.0.0", modDescription = "";

                using (var stream = new FileStream(inputPath, FileMode.Open, FileAccess.Read))
                using (var modReader = new FrostyModReader(stream))
                {
                    if (!modReader.IsValid) return 1;

                    var details = modReader.ReadModDetails();
                    modTitle = details.Title ?? "Unknown";
                    modAuthor = details.Author ?? "Unknown";
                    modCategory = details.Category ?? "";
                    modVersion = details.Version ?? "1.0.0";
                    modDescription = details.Description ?? "";

                    var resources = modReader.ReadResources();
                    Console.WriteLine($"Resources: {resources.Length}");

                    // Preliminary pass over resources to find Bundle names and add them to cache BEFORE processing
                    foreach (var res in resources)
                    {
                        if (res.Type == ModResourceType.Bundle)
                        {
                            AddToCache(res.Name);
                        }
                    }

                    Console.WriteLine("Processing resources...");
                    foreach (var resource in resources)
                    {
                        try
                        {
                            byte[] data = modReader.GetResourceData(resource);
                            var bundleList = resource.AddedBundles.ToList();
                            
                            bool hasBundles = bundleList.Count > 0;
                            bool hasData = data != null && data.Length > 0;

                            if (!hasData && !hasBundles && resource.Type != ModResourceType.Bundle && resource.Type != ModResourceType.Embedded) continue;

                            switch (resource.Type)
                            {
                                case ModResourceType.Ebx:
                                    ProcessEbx(resource, data, bundleList);
                                    break;
                                case ModResourceType.Res:
                                    ProcessRes(resource, data, bundleList);
                                    break;
                                case ModResourceType.Chunk:
                                    ProcessChunk(resource, data, bundleList);
                                    break;
                                case ModResourceType.Bundle:
                                    ProcessBundle(resource);
                                    break;
                                case ModResourceType.Embedded:
                                    ProcessEmbedded(resource, data);
                                    break;
                            }
                        }
                        catch (Exception) { }
                    }
                }

                // Final pass: Collect ALL unresolved hashes across all resources
                HashSet<int> extraBundles = new HashSet<int>();
                foreach(var e in ebxResources) foreach(var h in e.AddedBundles) if (!bundleHashToName.ContainsKey(h)) extraBundles.Add(h);
                foreach(var r in resResources) foreach(var h in r.AddedBundles) if (!bundleHashToName.ContainsKey(h)) extraBundles.Add(h);
                foreach(var c in chunkResources) foreach(var h in c.AddedBundles) if (!bundleHashToName.ContainsKey(h)) extraBundles.Add(h);
                
                foreach(int h in extraBundles) {
                    string hex = h.ToString("X8");
                    bundleHashToName[h] = hex;
                    bundleResources.Add(new BundleResourceData { Name = hex, SuperBundleName = "win32/superbundles/base/common", Type = 0, IsAdded = true });
                }

                Console.WriteLine("\nWriting .fbproject file...");
                WriteFbproject(outputPath, modTitle, modAuthor, modCategory, modVersion, modDescription, "");
                
                Console.WriteLine($"=== Success! ===\nCreated: {outputPath}");
                int ebxWithData = ebxResources.Count(e => e.Data != null);
                int ebxNoData = ebxResources.Count(e => e.Data == null);
                Console.WriteLine($"Processed: {ebxResources.Count} EBX ({ebxWithData} with data, {ebxNoData} bundle-only), {resResources.Count} RES, {chunkResources.Count} Chunks");
                Console.WriteLine($"Failed EBX parses: {failedEbxCount} (data lost, bundle refs preserved where possible)");
                
                return 0;
            }
            catch (Exception ex) {
                Console.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        static void ProcessEbx(BaseModResource resource, byte[] data, List<int> bundles)
        {
            byte[] processedData = data;
            string failReason = "unknown";
            
            // Handle null data (ResourceIndex = -1, no payload in mod)
            // These are typically bundle-only references or linked assets
            if (data == null)
            {
                failReason = "No data payload (bundle-only reference)";
                processedData = null;
            }
            else if (data.Length >= 4)
            {
                uint magic = BitConverter.ToUInt32(data, 0);
                if (magic != 0x0FB2D1CE && magic != 0x0FB4D1CE)
                {
                    try
                    {
                        using (var ms = new MemoryStream(data))
                        using (var casReader = new CasReader(ms))
                        {
                            processedData = casReader.Read();
                        }
                    }
                    catch (Exception ex) { failReason = $"CAS decompress: {ex.Message}"; }
                }
            }

            bool success = false;
            if (processedData != null && processedData.Length > 0)
            {
                try
                {
                    using (var ms = new MemoryStream(processedData))
                    using (var reader = EbxReader.CreateReader(ms))
                    {
                        if (reader.IsValid)
                        {
                            var asset = reader.ReadAsset<EbxAsset>();
                            if (asset != null && asset.IsValid)
                            {
                                using (var outStream = new MemoryStream())
                                using (var ebxWriter = EbxBaseWriter.CreateProjectWriter(outStream, EbxWriteFlags.IncludeTransient))
                                {
                                    ebxWriter.WriteAsset(asset);
                                    ebxResources.Add(new EbxResourceData
                                    {
                                        Name = resource.Name,
                                        Data = outStream.ToArray(),
                                        Guid = asset.FileGuid,
                                        IsAdded = resource.IsAdded,
                                        AddedBundles = bundles,
                                        HasCustomHandler = false
                                    });
                                    success = true;
                                }
                            }
                            else
                            {
                                failReason = asset == null ? "ReadAsset returned null" : "Asset not valid (empty objects)";
                            }
                        }
                        else
                        {
                            uint magic = processedData.Length >= 4 ? BitConverter.ToUInt32(processedData, 0) : 0;
                            failReason = $"EbxReader invalid (magic=0x{magic:X8}, len={processedData.Length})";
                        }
                    }
                }
                catch (Exception ex) { failReason = $"Parse exception: {ex.Message}"; }
            }

            if (!success)
            {
                 // IMPORTANT: For failed parses, we can't store the data because it's in
                 // game format, not project format. EbxReader.CreateProjectReader will crash.
                 // Instead, we store null data but keep the bundle assignments.
                 // This allows the mod to still add assets to bundles (common use case).
                 failedEbxCount++;
                 if (failedEbxCount <= 5)
                     Console.WriteLine($"  EBX FAIL [{failedEbxCount}]: {resource.Name} - {failReason}");
                 
                 if (bundles.Count > 0)
                 {
                     ebxResources.Add(new EbxResourceData
                     {
                         Name = resource.Name,
                         Data = null,  // No data = bundle-only modification
                         IsAdded = resource.IsAdded,
                         HasCustomHandler = false,
                         HandlerHash = 0,
                         AddedBundles = bundles
                     });
                 }
                 // If no bundles and parse failed, skip this asset entirely
            }
        }
        private static int failedEbxCount = 0;

        static void ProcessRes(BaseModResource resource, byte[] data, List<int> bundles)
        {
            var tempEntry = new ResAssetEntry();
            resource.FillAssetEntry(tempEntry);

            resResources.Add(new ResResourceData
            {
                Name = resource.Name,
                Data = data,
                ResRid = tempEntry.ResRid,
                ResType = tempEntry.ResType,
                ResMeta = tempEntry.ResMeta ?? new byte[0x10],
                IsAdded = resource.IsAdded,
                AddedBundles = bundles
            });
        }

        static void ProcessChunk(BaseModResource resource, byte[] data, List<int> bundles)
        {
            var tempEntry = new ChunkAssetEntry();
            resource.FillAssetEntry(tempEntry);

            chunkResources.Add(new ChunkResourceData
            {
                Id = tempEntry.Id,
                Data = data,
                H32 = tempEntry.H32,
                RangeStart = tempEntry.RangeStart,
                RangeEnd = tempEntry.RangeEnd,
                LogicalOffset = tempEntry.LogicalOffset,
                LogicalSize = tempEntry.LogicalSize,
                FirstMip = tempEntry.FirstMip,
                IsAdded = resource.IsAdded,
                AddedBundles = bundles
            });
        }

        static void ProcessBundle(BaseModResource resource)
        {
            var tempEntry = new BundleEntry();
            resource.FillAssetEntry(tempEntry);

            // Use the ACTUAL superbundle name from our cache, or the default from game
            // This prevents crash when Frosty can't find the superbundle by name
            string sbName = defaultSuperBundleName ?? "win32/globals";
            if (superBundleIdToName.ContainsKey(tempEntry.SuperBundleId))
            {
                sbName = superBundleIdToName[tempEntry.SuperBundleId];
            }

            bundleResources.Add(new BundleResourceData
            {
                Name = tempEntry.Name,
                SuperBundleName = sbName,
                IsAdded = true
            });
        }

        static void ProcessEmbedded(BaseModResource resource, byte[] data)
        {
            if (data == null || data.Length == 0) return;

            if (resource.Name.Equals("Icon", StringComparison.OrdinalIgnoreCase))
            {
                projectIcon = data;
            }
            else if (resource.Name.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase))
            {
                projectScreenshots.Add(data);
            }
        }

        static void WriteFbproject(string outputPath, string title, string author, string category, string version, string description, string link)
        {
            using (var writer = new NativeWriter(new FileStream(outputPath, FileMode.Create)))
            {
                writer.Write(FBPROJECT_MAGIC);
                writer.Write(FBPROJECT_VERSION);
                writer.WriteNullTerminatedString(ProfilesLibrary.ProfileName);
                writer.Write(DateTime.Now.Ticks); // Created
                writer.Write(DateTime.Now.Ticks); // Modified
                writer.Write((uint)0); // Unused

                writer.WriteNullTerminatedString(title);
                writer.WriteNullTerminatedString(author);
                writer.WriteNullTerminatedString(category);
                writer.WriteNullTerminatedString(version);
                writer.WriteNullTerminatedString(description);

                // Icon
                if (projectIcon != null) {
                    writer.Write(projectIcon.Length);
                    writer.Write(projectIcon);
                } else {
                    writer.Write(0);
                }

                // Screenshots (up to 4)
                for (int i = 0; i < 4; i++) {
                    if (i < projectScreenshots.Count) {
                        writer.Write(projectScreenshots[i].Length);
                        writer.Write(projectScreenshots[i]);
                    } else {
                        writer.Write(0);
                    }
                }

                writer.Write(0); // Superbundles (unused)

                // Bundles - SKIP writing bundles to avoid superbundle name crash
                // Bundle references in asset AddedBundles are resolved by hash anyway
                // If we write bundles with invalid superbundle names, Frosty crashes on SaveToMod
                writer.Write(0); // Bundle count = 0
                Console.WriteLine($"Skipping {bundleResources.Count} bundles (superbundle resolution not available)");

                // Added EBX
                writer.Write(ebxResources.Count(e => e.IsAdded));
                foreach (var ebx in ebxResources.Where(e => e.IsAdded))
                {
                    writer.WriteNullTerminatedString(ebx.Name);
                    writer.Write(ebx.Guid);
                }

                // Added RES
                writer.Write(resResources.Count(r => r.IsAdded));
                foreach (var res in resResources.Where(r => r.IsAdded))
                {
                    writer.WriteNullTerminatedString(res.Name);
                    writer.Write(res.ResRid);
                    writer.Write(res.ResType);
                    writer.Write(res.ResMeta);
                }

                // Added Chunks
                writer.Write(chunkResources.Count(c => c.IsAdded));
                foreach (var chunk in chunkResources.Where(c => c.IsAdded))
                {
                    writer.Write(chunk.Id);
                    writer.Write(chunk.H32);
                }

                // EBX
                writer.Write(ebxResources.Count);
                foreach (var ebx in ebxResources)
                {
                    writer.WriteNullTerminatedString(ebx.Name);
                    writer.Write(0); // Linked
                    writer.Write(ebx.AddedBundles.Count);
                    foreach(int hash in ebx.AddedBundles) {
                        string bName = bundleHashToName.ContainsKey(hash) ? bundleHashToName[hash] : hash.ToString("X8");
                        writer.WriteNullTerminatedString(bName);
                    }
                    bool hasData = ebx.Data != null && ebx.Data.Length > 0;
                    writer.Write(hasData);
                    if (hasData) {
                        writer.Write(false); // transient
                        writer.WriteNullTerminatedString(""); // userdata
                        writer.Write(ebx.HasCustomHandler); // CUSTOM HANDLER FLAG
                        writer.Write(ebx.Data.Length);
                        writer.Write(ebx.Data);
                    }
                }

                // RES
                writer.Write(resResources.Count);
                foreach (var res in resResources)
                {
                    writer.WriteNullTerminatedString(res.Name);
                    writer.Write(0); // Linked
                    writer.Write(res.AddedBundles.Count);
                    foreach(int hash in res.AddedBundles) {
                        string bName = bundleHashToName.ContainsKey(hash) ? bundleHashToName[hash] : hash.ToString("X8");
                        writer.WriteNullTerminatedString(bName);
                    }
                    bool hasData = res.Data != null && res.Data.Length > 0;
                    writer.Write(hasData);
                    if (hasData) {
                         byte[] s = new byte[20]; s[0]=1; writer.Write(s); // sha1
                         writer.Write((long)res.Data.Length);
                         writer.Write(res.ResMeta.Length); writer.Write(res.ResMeta);
                         writer.WriteNullTerminatedString("");
                         writer.Write(res.Data.Length);
                         writer.Write(res.Data);
                    }
                }

                // Chunk
                writer.Write(chunkResources.Count);
                foreach (var chunk in chunkResources)
                {
                    writer.Write(chunk.Id);
                    writer.Write(chunk.AddedBundles.Count);
                    foreach(int hash in chunk.AddedBundles) {
                        string bName = bundleHashToName.ContainsKey(hash) ? bundleHashToName[hash] : hash.ToString("X8");
                        writer.WriteNullTerminatedString(bName);
                    }
                    writer.Write(chunk.FirstMip);
                    writer.Write(chunk.H32);
                    bool hasData = chunk.Data != null && chunk.Data.Length > 0;
                    writer.Write(hasData);
                    if (hasData) {
                        byte[] s = new byte[20]; s[0]=1; writer.Write(s); // sha1
                        writer.Write(chunk.LogicalOffset); writer.Write(chunk.LogicalSize);
                        writer.Write(chunk.RangeStart); writer.Write(chunk.RangeEnd);
                        writer.Write(false); // addtochunkbundle
                        writer.WriteNullTerminatedString("");
                        writer.Write(chunk.Data.Length);
                        writer.Write(chunk.Data);
                    }
                }

                writer.Write(0); // EOF
            }
        }

        static void CompileProject(string projectPath, string fbmodPath) {
            Console.WriteLine("Initializing Compiler SDK...");
            TypeLibrary.Initialize();
            App.AssetManager = new AssetManager(App.FileSystem, App.ResourceManager);
            App.AssetManager.Initialize();

            using (NativeReader reader = new NativeReader(new FileStream(projectPath, FileMode.Open))) {
                if (reader.ReadULong() != FBPROJECT_MAGIC) throw new Exception("Invalid magic");
                reader.ReadUInt(); reader.ReadNullTerminatedString(); reader.ReadLong(); reader.ReadLong(); reader.ReadUInt();
                reader.ReadNullTerminatedString(); reader.ReadNullTerminatedString(); reader.ReadNullTerminatedString();
                reader.ReadNullTerminatedString(); reader.ReadNullTerminatedString();
                int iconLen = reader.ReadInt(); if (iconLen > 0) reader.ReadBytes(iconLen);
                for (int i=0; i<4; i++) { int sLen = reader.ReadInt(); if (sLen > 0) reader.ReadBytes(sLen); }
                int sbCount = reader.ReadInt();
                int bundleCount = reader.ReadInt();
                for (int i=0; i<bundleCount; i++) {
                    string bName = reader.ReadNullTerminatedString();
                    string super = reader.ReadNullTerminatedString();
                    int type = reader.ReadInt();
                    int sbId = App.AssetManager.GetSuperBundleId(super);
                    App.AssetManager.AddBundle(bName, (BundleType)type, sbId);
                }
                int addedEbxCount = reader.ReadInt();
                for(int i=0; i<addedEbxCount; i++) {
                     string name = reader.ReadNullTerminatedString();
                     Guid guid = reader.ReadGuid();
                     App.AssetManager.AddEbx(new EbxAssetEntry { Name = name, Guid = guid, IsAdded = true });
                }
                int addedResCount = reader.ReadInt();
                for(int i=0; i<addedResCount; i++) {
                    string name = reader.ReadNullTerminatedString();
                    ulong rid = reader.ReadULong();
                    uint type = reader.ReadUInt();
                    byte[] meta = reader.ReadBytes(0x10);
                    App.AssetManager.AddRes(new ResAssetEntry { Name = name, ResRid = rid, ResType = type, ResMeta = meta, IsAdded = true });
                }
                int addedChunkCount = reader.ReadInt();
                for(int i=0; i<addedChunkCount; i++) {
                    Guid id = reader.ReadGuid();
                    int h32 = reader.ReadInt();
                    App.AssetManager.AddChunk(new ChunkAssetEntry { Id = id, H32 = h32, IsAdded = true });
                }
                int ebxs = reader.ReadInt();
                for (int i=0; i<ebxs; i++) {
                    string name = reader.ReadNullTerminatedString();
                    reader.ReadInt();
                    int abCount = reader.ReadInt();
                    for(int j=0; j<abCount; j++) {
                        string bName = reader.ReadNullTerminatedString();
                        if (App.AssetManager.GetBundleId(bName) == -1) Console.WriteLine($"CRASH RISK: Invalid Bundle Link '{bName}' for {name}");
                    }
                    if (reader.ReadBoolean()) { reader.ReadBoolean(); reader.ReadNullTerminatedString();
                        if (reader.ReadBoolean()) { int dLen = reader.ReadInt(); reader.ReadBytes(dLen); }
                        else { int dLen = reader.ReadInt(); reader.ReadBytes(dLen); }
                    }
                }
            }
            Console.WriteLine("Project Check Completed.");
        }

        static string FindGameExecutable(string path) {
             if (File.Exists(Path.Combine(path, "GW2.Main_Win64_Retail.exe"))) return Path.Combine(path, "GW2.Main_Win64_Retail.exe");
             return Directory.GetFiles(path, "*.exe").FirstOrDefault();
        }
    }
}
