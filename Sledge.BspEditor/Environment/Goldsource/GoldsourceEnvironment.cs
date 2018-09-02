using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LogicAndTrick.Oy;
using Sledge.BspEditor.Compile;
using Sledge.BspEditor.Documents;
using Sledge.BspEditor.Primitives;
using Sledge.BspEditor.Primitives.MapData;
using Sledge.BspEditor.Primitives.MapObjectData;
using Sledge.BspEditor.Primitives.MapObjects;
using Sledge.BspEditor.Providers;
using Sledge.Common;
using Sledge.Common.Shell.Commands;
using Sledge.DataStructures.GameData;
using Sledge.FileSystem;
using Sledge.Providers.GameData;
using Sledge.Providers.Texture;
using Path = System.IO.Path;

namespace Sledge.BspEditor.Environment.Goldsource
{
    public class GoldsourceEnvironment : IEnvironment
    {
        private readonly ITexturePackageProvider _wadProvider;
        private readonly ITexturePackageProvider _spriteProvider;
        private readonly IGameDataProvider _fgdProvider;
        private readonly Lazy<Task<TextureCollection>> _textureCollection;
        private readonly List<IEnvironmentData> _data;
        private readonly Lazy<Task<GameData>> _gameData;

        public string Engine => "Goldsource";
        public string ID { get; set; }
        public string Name { get; set; }

        public string BaseDirectory { get; set; }
        public string GameDirectory { get; set; }
        public string ModDirectory { get; set; }
        public string GameExe { get; set; }
        public bool LoadHdModels { get; set; }

        public List<string> FgdFiles { get; set; }
        public bool IncludeFgdDirectoriesInEnvironment { get; set; }
        public string DefaultPointEntity { get; set; }
        public string DefaultBrushEntity { get; set; }
        public bool OverrideMapSize { get; set; }
        public decimal MapSizeLow { get; set; }
        public decimal MapSizeHigh { get; set; }

        public decimal DefaultTextureScale { get; set; }

        public string ToolsDirectory { get; set; }
        public bool IncludeToolsDirectoryInEnvironment { get; set; }

        public string CsgExe { get; set; }
        public string VisExe { get; set; }
        public string BspExe { get; set; }
        public string RadExe { get; set; }

        public bool GameCopyBsp { get; set; }
        public bool GameRun { get; set; }
        public bool GameAsk { get; set; }

        public bool MapCopyBsp { get; set; }
        public bool MapCopyMap { get; set; }
        public bool MapCopyLog { get; set; }
        public bool MapCopyErr { get; set; }
        public bool MapCopyRes { get; set; }

        public List<string> ExcludedWads { get; set; }

        private IFile _root;

        public IFile Root
        {
            get
            {
                if (_root == null)
                {
                    var dirs = Directories.Where(Directory.Exists).ToList();
                    if (dirs.Any()) _root = new RootFile(Name, dirs.Select(x => new NativeFile(x)));
                    else _root = new VirtualFile(null, "");
                }
                return _root;
            }
        }

        public IEnumerable<string> Directories
        {
            get
            {
                // mod_addon (custom content)
                yield return Path.Combine(BaseDirectory, ModDirectory + "_addon");

                //mod_downloads (downloaded content)
                yield return Path.Combine(BaseDirectory, ModDirectory + "_downloads");

                // mod_hd (high definition content)
                yield return Path.Combine(BaseDirectory, ModDirectory + "_hd");

                // mod (base mod content)
                yield return Path.Combine(BaseDirectory, ModDirectory);

                if (!String.Equals(GameDirectory, ModDirectory, StringComparison.CurrentCultureIgnoreCase))
                {
                    yield return Path.Combine(BaseDirectory, GameDirectory + "_addon");
                    yield return Path.Combine(BaseDirectory, GameDirectory + "_downloads");
                    yield return Path.Combine(BaseDirectory, GameDirectory + "_hd");
                    yield return Path.Combine(BaseDirectory, GameDirectory);
                }
                
                if (IncludeToolsDirectoryInEnvironment && !String.IsNullOrWhiteSpace(ToolsDirectory) && Directory.Exists(ToolsDirectory))
                {
                    yield return ToolsDirectory;
                }

                if (IncludeFgdDirectoriesInEnvironment)
                {
                    foreach (var file in FgdFiles)
                    {
                        if (File.Exists(file)) yield return Path.GetDirectoryName(file);
                    }
                }

                // Editor location to the path, for sprites and the like
                yield return Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            }
        }

        public GoldsourceEnvironment()
        {
            _wadProvider = Container.Get<ITexturePackageProvider>("Wad3");
            _spriteProvider = Container.Get<ITexturePackageProvider>("Spr");
            _fgdProvider = Container.Get<IGameDataProvider>("Fgd");

            _textureCollection = new Lazy<Task<TextureCollection>>(MakeTextureCollectionAsync);
            _gameData = new Lazy<Task<GameData>>(MakeGameDataAsync);
            _data = new List<IEnvironmentData>();
            FgdFiles = new List<string>();
            ExcludedWads = new List<string>();
            IncludeToolsDirectoryInEnvironment = IncludeToolsDirectoryInEnvironment = true;
        }

        private async Task<TextureCollection> MakeTextureCollectionAsync()
        {
            var wadRefs = _wadProvider.GetPackagesInFile(Root).Where(x => !ExcludedWads.Contains(x.Name, StringComparer.InvariantCultureIgnoreCase));
            var wads = await _wadProvider.GetTexturePackages(wadRefs);

            var spriteRefs = _spriteProvider.GetPackagesInFile(Root);
            var sprites = await _spriteProvider.GetTexturePackages(spriteRefs);

            return new GoldsourceTextureCollection(wads.Union(sprites));
        }

        private async Task<GameData> MakeGameDataAsync()
        {
            return _fgdProvider.GetGameDataFromFiles(FgdFiles);
        }

        public Task<TextureCollection> GetTextureCollection()
        {
            return _textureCollection.Value;
        }

        public Task<GameData> GetGameData()
        {
            return _gameData.Value;
        }

        public async Task UpdateDocumentData(MapDocument document)
        {
            var tc = await GetTextureCollection();

            // Get the list of used packages - the packages are abstracted away from the file system, so we don't know where they are located yet
            var usedPackages = GetUsedTexturePackages(document, tc).Select(x => x.Location).ToHashSet(StringComparer.InvariantCultureIgnoreCase);

            // Get the list of wad locations - for the wad texture provider, this is a quick operation
            var wads = _wadProvider.GetPackagesInFile(Root).Select(x => x.File.GetPathOnDisk()).Where(x => x != null).ToList();

            // Get the list of wads that are in the used set
            var usedWads = wads.Where(x => usedPackages.Contains(Path.GetFileName(x))).ToList();

            document.Map.Root.Data.GetOne<EntityData>()?.Set("wad", string.Join(";", usedWads));
        }

        private IEnumerable<string> GetUsedTextures(MapDocument document)
        {
            return document.Map.Root.FindAll().SelectMany(x => x.Data.OfType<ITextured>()).Select(x => x.Texture.Name).Distinct();
        }

        private IEnumerable<TexturePackage> GetUsedTexturePackages(MapDocument document, TextureCollection collection)
        {
            var used = GetUsedTextures(document).ToList();
            return collection.Packages.Where(x => used.Any(x.HasTexture));
        }

        public void AddData(IEnvironmentData data)
        {
            if (!_data.Contains(data)) _data.Add(data);
        }

        public IEnumerable<T> GetData<T>() where T : IEnvironmentData
        {
            return _data.OfType<T>();
        }

        public async Task<Batch> CreateBatch(IEnumerable<BatchArgument> arguments)
        {
            var args = arguments.GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.First().Arguments);

            var batch = new Batch();

            // Create the working directory
            batch.Steps.Add(new BatchCallback(async (b, d) =>
            {
                var workingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                if (!Directory.Exists(workingDir)) Directory.CreateDirectory(workingDir);
                b.Variables["WorkingDirectory"] = workingDir;

                await Oy.Publish("Compile:Debug", $"Working directory is: {workingDir}\r\n");
            }));

            // Save the file to the working directory
            batch.Steps.Add(new BatchCallback(async (b, d) =>
            {
                var fn = d.FileName;
                if (String.IsNullOrWhiteSpace(fn) || fn.IndexOf('.') < 0) fn = Path.GetRandomFileName();
                var mapFile = Path.GetFileNameWithoutExtension(fn) + ".map";
                b.Variables["MapFileName"] = mapFile;

                var path = Path.Combine(b.Variables["WorkingDirectory"], mapFile);
                b.Variables["MapFile"] = path;

                await Oy.Publish("Command:Run", new CommandMessage("Internal:ExportDocument", new
                {
                    Document = d,
                    Path = path,
                    LoaderHint = nameof(MapBspSourceProvider)
                }));

                await Oy.Publish("Compile:Debug", $"Map file is: {path}\r\n");
            }));

            // Run the compile tools
            if (args.ContainsKey("CSG")) batch.Steps.Add(new BatchProcess(Path.Combine(ToolsDirectory, CsgExe), args["CSG"] + " \"{MapFile}\""));
            if (args.ContainsKey("BSP")) batch.Steps.Add(new BatchProcess(Path.Combine(ToolsDirectory, BspExe), args["BSP"] + " \"{MapFile}\""));
            if (args.ContainsKey("VIS")) batch.Steps.Add(new BatchProcess(Path.Combine(ToolsDirectory, VisExe), args["VIS"] + " \"{MapFile}\""));
            if (args.ContainsKey("RAD")) batch.Steps.Add(new BatchProcess(Path.Combine(ToolsDirectory, RadExe), args["RAD"] + " \"{MapFile}\""));

            // Check for errors
            batch.Steps.Add(new BatchCallback(async (b, d) =>
            {
                var errFile = Path.ChangeExtension(b.Variables["MapFile"], "err");
                if (errFile != null && File.Exists(errFile))
                {
                    var errors = File.ReadAllText(errFile);
                    b.Successful = false;
                    await Oy.Publish("Compile:Error", errors);
                }

                var bspFile = Path.ChangeExtension(b.Variables["MapFile"], "bsp");
                if (bspFile != null && !File.Exists(bspFile))
                {
                    b.Successful = false;
                }
            }));

            // Copy resulting files around
            batch.Steps.Add(new BatchCallback(async (b, d) =>
            {
                var mapDir = Path.GetDirectoryName(d.FileName);
                var gameMapDir = Path.Combine(BaseDirectory, ModDirectory, "maps");

                // Copy configured files to the map directory
                CopyFile(MapCopyBsp, "bsp", mapDir);
                CopyFile(MapCopyMap, "map", mapDir);
                CopyFile(MapCopyRes, "res", mapDir);
                CopyFile(MapCopyErr, "err", mapDir);
                CopyFile(MapCopyLog, "log", mapDir);

                // Always copy pointfiles if they exist
                CopyFile(true, "lin", mapDir);
                CopyFile(true, "pts", mapDir);

                // Copy the BSP/RES to the game dir if configured
                CopyFile(b.Successful && GameCopyBsp, "bsp", gameMapDir);
                CopyFile(b.Successful && GameCopyBsp, "res", gameMapDir);

                void CopyFile(bool test, string extension, string directory)
                {
                    if (!test || directory == null || !Directory.Exists(directory)) return;

                    var file = Path.ChangeExtension(b.Variables["MapFile"], extension);
                    if (file == null || !File.Exists(file)) return;

                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
                }
            }));

            // Delete temp directory
            batch.Steps.Add(new BatchCallback(async (b, d) =>
            {
                var workingDir = b.Variables["WorkingDirectory"];
                if (Directory.Exists(workingDir)) Directory.Delete(workingDir, true);
            }));

            if (GameRun)
            {
                batch.Steps.Add(new BatchCallback(async (b, d) =>
                {
                    if (!b.Successful) return;

                    if (GameAsk)
                    {
                        var ask = MessageBox.Show(
                            $"The compile of {d.Name} completed successfully.\nWould you like to run the game now?",
                            "Compile Successful!",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question
                        );
                        if (ask != DialogResult.Yes) return;
                    }

                    var exe = Path.Combine(BaseDirectory, GameExe);
                    if (!File.Exists(exe))
                    {
                        MessageBox.Show(
                            "The location of the game executable is incorrect. Please ensure that the game configuration has been set up correctly.",
                            "Failed to launch!",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                        return;
                    }

                    var gameArg = ModDirectory == "valve" ? "" : $"-game {ModDirectory} ";
                    var mapName = Path.GetFileNameWithoutExtension(b.Variables["MapFileName"]);

                    var flags = String.Format("{0}-dev -console +map \"{1}\"", gameArg, mapName);
                    try
                    {
                        Process.Start(exe, flags);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Launching game failed: " + ex.Message, "Failed to launch!", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }));
            }

            return batch;
        }

        private static readonly string AutoVisgroupPrefix = typeof(GoldsourceEnvironment).Namespace + ".AutomaticVisgroups";

        public IEnumerable<AutomaticVisgroup> GetAutomaticVisgroups()
        {
            // Entities
            yield return new AutomaticVisgroup(x => x is Entity && x.Hierarchy.HasChildren)
            {
                Path = $"{AutoVisgroupPrefix}.Entities",
                Key = $"{AutoVisgroupPrefix}.BrushEntities"
            };
            yield return new AutomaticVisgroup(x => x is Entity && !x.Hierarchy.HasChildren)
            {
                Path = $"{AutoVisgroupPrefix}.Entities",
                Key = $"{AutoVisgroupPrefix}.PointEntities"
            };
            yield return new AutomaticVisgroup(x => x is Entity e && e.EntityData.Name.StartsWith("light", StringComparison.InvariantCultureIgnoreCase))
            {
                Path = $"{AutoVisgroupPrefix}.Entities",
                Key = $"{AutoVisgroupPrefix}.Lights"
            };
            yield return new AutomaticVisgroup(x => x is Entity e && e.EntityData.Name.StartsWith("trigger_", StringComparison.InvariantCultureIgnoreCase))
            {
                Path = $"{AutoVisgroupPrefix}.Entities",
                Key = $"{AutoVisgroupPrefix}.Triggers"
            };
            yield return new AutomaticVisgroup(x => x is Entity e && e.EntityData.Name.IndexOf("_node", StringComparison.InvariantCultureIgnoreCase) >= 0)
            {
                Path = $"{AutoVisgroupPrefix}.Entities",
                Key = $"{AutoVisgroupPrefix}.Nodes"
            };
            
            // Tool brushes
            yield return new AutomaticVisgroup(x => x is Solid s && s.Faces.Any(f => string.Equals(f.Texture.Name, "bevel", StringComparison.InvariantCultureIgnoreCase)))
            {
                Path = $"{AutoVisgroupPrefix}.ToolBrushes",
                Key = $"{AutoVisgroupPrefix}.Bevel"
            };
            yield return new AutomaticVisgroup(x => x is Solid s && s.Faces.Any(f => string.Equals(f.Texture.Name, "hint", StringComparison.InvariantCultureIgnoreCase)))
            {
                Path = $"{AutoVisgroupPrefix}.ToolBrushes",
                Key = $"{AutoVisgroupPrefix}.Hint"
            };
            yield return new AutomaticVisgroup(x => x is Solid s && s.Faces.Any(f => string.Equals(f.Texture.Name, "origin", StringComparison.InvariantCultureIgnoreCase)))
            {
                Path = $"{AutoVisgroupPrefix}.ToolBrushes",
                Key = $"{AutoVisgroupPrefix}.Origin"
            };
            yield return new AutomaticVisgroup(x => x is Solid s && s.Faces.Any(f => string.Equals(f.Texture.Name, "skip", StringComparison.InvariantCultureIgnoreCase)))
            {
                Path = $"{AutoVisgroupPrefix}.ToolBrushes",
                Key = $"{AutoVisgroupPrefix}.Skip"
            };
            yield return new AutomaticVisgroup(x => x is Solid s && s.Faces.Any(f => string.Equals(f.Texture.Name, "aaatrigger", StringComparison.InvariantCultureIgnoreCase)))
            {
                Path = $"{AutoVisgroupPrefix}.ToolBrushes",
                Key = $"{AutoVisgroupPrefix}.Trigger"
            };

            // World geometry
            yield return new AutomaticVisgroup(x => x is Solid s && s.FindClosestParent(p => p is Entity) == null)
            {
                Path = $"{AutoVisgroupPrefix}.WorldGeometry",
                Key = $"{AutoVisgroupPrefix}.Brushes"
            };
            yield return new AutomaticVisgroup(x => x is Solid s && s.FindClosestParent(p => p is Entity) == null && s.Faces.Any(f => string.Equals(f.Texture.Name, "null", StringComparison.InvariantCultureIgnoreCase)))
            {
                Path = $"{AutoVisgroupPrefix}.WorldGeometry",
                Key = $"{AutoVisgroupPrefix}.Null"
            };
            yield return new AutomaticVisgroup(x => x is Solid s && s.FindClosestParent(p => p is Entity) == null && s.Faces.Any(f => string.Equals(f.Texture.Name, "sky", StringComparison.InvariantCultureIgnoreCase)))
            {
                Path = $"{AutoVisgroupPrefix}.WorldGeometry",
                Key = $"{AutoVisgroupPrefix}.Sky"
            };
            yield return new AutomaticVisgroup(x => x is Solid s && s.FindClosestParent(p => p is Entity) == null && s.Faces.Any(f => f.Texture.Name.StartsWith("!")))
            {
                Path = $"{AutoVisgroupPrefix}.WorldGeometry",
                Key = $"{AutoVisgroupPrefix}.Water"
            };
        }
    }
}